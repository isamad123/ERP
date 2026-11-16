using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.BuildingBlocks.Domain.Time;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Infrastructure;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.IntegrationTests.Access;

/// <summary>
/// The access-control tests (slice S1.06) run the real API pipeline with the REAL access engine (no allow-all doubles)
/// against customers provisioned for them alone, so changing baseline roles or the tenant admins never disturbs the
/// S1.03 and S1.05 tests that share the same PostgreSQL fixture.
/// </summary>
public sealed class AccessHarness
{
    private static readonly ConcurrentDictionary<TenancyFixture, Lazy<Task<AccessHarness>>> Instances = new();

    private readonly TenancyFixture _fx;
    private int _counter = 100_000;

    private AccessHarness(TenancyFixture fx, WebApplication app, TestClock clock, string code)
    {
        _fx = fx;
        App = app;
        Clock = clock;
        Code = code;
    }

    public WebApplication App { get; }

    public TestClock Clock { get; }

    /// <summary>The customer these tests own.</summary>
    public string Code { get; }

    public TenancyFixture Fixture => _fx;

    public static Task<AccessHarness> ForAsync(TenancyFixture fx) =>
        Instances.GetOrAdd(fx, f => new Lazy<Task<AccessHarness>>(() => StartAsync(f))).Value;

    private static async Task<AccessHarness> StartAsync(TenancyFixture fx)
    {
        var clock = new TestClock();
        var code = "s" + fx.Suffix;
        await fx.ProvisionAsync(code, CancellationToken.None);
        var app = await fx.StartAppAsync(null, CancellationToken.None, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        }, realAccess: true);
        return new AccessHarness(fx, app, clock, code);
    }

    /// <summary>Another customer provisioned for one test (the last-tenant-admin rule needs a known set of admins).</summary>
    public async Task<string> ProvisionAsync(string prefix, CancellationToken ct)
    {
        var code = prefix + _fx.Suffix;
        await _fx.ProvisionAsync(code, ct);
        return code;
    }

    public Task<NpgsqlConnection> OwnerAsync(string? code = null, CancellationToken ct = default) => _fx.OpenTenantAsync(code ?? Code, TenantLogins.Owner, ct);

    public string Token(Guid userId, string? code = null) => _fx.Token(code ?? Code, user: userId);

    // ------------------------------------------------------------------ HTTP

    public Task<Answer> GetAsync(Guid user, string path, CancellationToken ct, string? code = null) => SendAsync(HttpMethod.Get, user, path, null, code, ct);

    public Task<Answer> PostAsync(Guid user, string path, object? body, CancellationToken ct, string? code = null) => SendAsync(HttpMethod.Post, user, path, body ?? new { }, code, ct);

    public Task<Answer> PutAsync(Guid user, string path, object body, CancellationToken ct, string? code = null) => SendAsync(HttpMethod.Put, user, path, body, code, ct);

    public Task<Answer> SendDeleteAsync(Guid user, string path) => SendAsync(HttpMethod.Delete, user, path, null, null, CancellationToken.None);

    private async Task<Answer> SendAsync(HttpMethod method, Guid user, string path, object? body, string? code, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(App, TenancyFixture.HostOf(code ?? Code), Token(user, code));
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = body is string json ? new StringContent(json, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body, options: JsonSerializerOptions.Web);
        }

        using var response = await client.SendAsync(request, ct);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    // ------------------------------------------------------------------ users, roles, scopes (written as the database owner would)

    public static DateOnly Day(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    public DateOnly Today => Day(Clock.UtcNow);

    /// <summary>An active user holding the given baseline roles for all companies.</summary>
    public async Task<Guid> UserAsync(CancellationToken ct, string? code = null, params string[] roleCodes)
    {
        var id = Guid.CreateVersion7();
        var name = "acc" + Guid.NewGuid().ToString("N")[..12];
        await using var c = await OwnerAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@access.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, ct, id, name, BaselineSeed.SystemUserId);
        foreach (var role in roleCodes)
        {
            await AssignAsync(c, id, await RoleIdAsync(c, role, ct), null, Today.AddDays(-1), null, ct);
        }

        return id;
    }

    public static async Task<Guid> RoleIdAsync(NpgsqlConnection c, string code, CancellationToken ct) =>
        await ScalarAsync<Guid>(c, "select id from iam.roles where code = $1 and not is_deleted", ct, code);

    public async Task<Guid> RoleIdAsync(string code, CancellationToken ct, string? tenantCode = null)
    {
        await using var c = await OwnerAsync(tenantCode, ct);
        return await RoleIdAsync(c, code, ct);
    }

    /// <summary>A customer role with permissions, data scopes (resource → scope code or data_scopes id) and field rules.</summary>
#pragma warning disable CA1068 // test helper: the token first keeps the many role definitions in the tests readable
    public async Task<Guid> RoleAsync(CancellationToken ct, string[] permissions, (string Resource, object Scope)[]? scopes = null, (string Resource, string Field, string Access)[]? fields = null, string? code = null)
    {
        var id = Guid.CreateVersion7();
        await using var c = await OwnerAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Test role"}', 'tenant', now(), $3, now(), $3)
            """, ct, id, "r" + Guid.NewGuid().ToString("N")[..12], BaselineSeed.SystemUserId);
        await ExecAsync(c, "insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = any($2)", ct, id, permissions);
        Assert.Equal(permissions.Length, await ScalarAsync<long>(c, "select count(*) from iam.role_permissions where role_id = $1", ct, id));
        foreach (var (resource, scope) in scopes ?? [])
        {
            var scopeId = scope is Guid g ? g : await ScalarAsync<Guid>(c, "select id from iam.data_scopes where code = $1", ct, (string)scope);
            await ExecAsync(c, "insert into iam.role_data_scopes (role_id, resource_type, data_scope_id) values ($1, $2, $3)", ct, id, resource, scopeId);
        }

        foreach (var (resource, field, access) in fields ?? [])
        {
            await ExecAsync(c, """
                insert into iam.field_policies (id, role_id, resource_type, field_key, access, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, now(), $6, now(), $6)
                """, ct, Guid.CreateVersion7(), id, resource, field, access, BaselineSeed.SystemUserId);
        }

        return id;
    }
#pragma warning restore CA1068

    /// <summary>A data scope of a kind with parameters (definition JSON), as the scope designer (S1.13) will store it.</summary>
    public async Task<Guid> ScopeAsync(string kind, object definition, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await using var c = await OwnerAsync(null, ct);
        await ExecAsync(c, """
            insert into iam.data_scopes (id, code, name_i18n, kind, definition, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Test scope"}', $3, $4::jsonb, now(), $5, now(), $5)
            """, ct, id, "s" + Guid.NewGuid().ToString("N")[..12], kind, JsonSerializer.Serialize(definition), BaselineSeed.SystemUserId);
        return id;
    }

    public async Task<Guid> AssignAsync(Guid user, Guid role, Guid? company, DateOnly from, DateOnly? to, CancellationToken ct, string? code = null)
    {
        await using var c = await OwnerAsync(code, ct);
        return await AssignAsync(c, user, role, company, from, to, ct);
    }

    private static async Task<Guid> AssignAsync(NpgsqlConnection c, Guid user, Guid role, Guid? company, DateOnly from, DateOnly? to, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await ExecAsync(c, """
            insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, valid_to, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, 'test', $5, $6, now(), $7, now(), $7)
            """, ct, id, user, role, company, from, to, BaselineSeed.SystemUserId);
        return id;
    }

    public async Task ExecAsync(string sql, CancellationToken ct, params object?[] values)
    {
        await using var c = await OwnerAsync(null, ct);
        await ExecAsync(c, sql, ct, values);
    }

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params object?[] values)
    {
        await using var c = await OwnerAsync(null, ct);
        return await ScalarAsync<T>(c, sql, ct, values);
    }

    public static async Task ExecAsync(NpgsqlConnection c, string sql, CancellationToken ct, params object?[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, c);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlConnection c, string sql, CancellationToken ct, params object?[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, c);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    // ------------------------------------------------------------------ business rows (any generated table, as RowFactory writes them)

    /// <summary>The customer's default company (created by provisioning).</summary>
    public Task<Guid> DefaultCompanyAsync(CancellationToken ct) => ScalarAsync<Guid>("select id from org.companies where not is_deleted order by created_at, id limit 1", ct);

    /// <summary>One committed row of a table with chosen values; company-scoped rows belong to <paramref name="company"/>.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> RowAsync(string table, Guid company, IReadOnlyDictionary<string, object?>? overrides, CancellationToken ct)
    {
        await using var c = await OwnerAsync(null, ct);
        var expected = MigratedDatabases.Catalog.Tables.Single(t => t.Database == "tenant" && t.FullName == table);
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            var factory = new RowFactory(c, tx, MigratedDatabases.Catalog, _counter, company, attempt - 1);
            try
            {
                var row = await factory.InsertAsync(expected, overrides, ct);
                await tx.CommitAsync(ct);
                _counter = factory.LastValue + 1;
                return row;
            }
            catch (PostgresException e) when (attempt < 6 && e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation)
            {
                await tx.RollbackAsync(ct);
                _counter = factory.LastValue + 1 + 997;
            }
        }
    }

    /// <summary>A new company (its default branch is created by the database, rule SC-2).</summary>
    public async Task<Guid> CompanyAsync(CancellationToken ct)
    {
        var row = await RowAsync("org.companies", Guid.Empty, new Dictionary<string, object?> { ["status"] = "active" }, ct);
        return (Guid)row["id"]!;
    }
}
