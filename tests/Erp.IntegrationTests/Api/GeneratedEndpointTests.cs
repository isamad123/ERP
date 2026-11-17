using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Migrations;
using Erp.CodeGen.Dictionary;
using Erp.Hrm.Api.Generated;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.IntegrationTests.Infrastructure;
using Erp.Kernel.Api.Generated;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Api;

/// <summary>
/// The generated repositories and minimal API endpoints, run in process against a real PostgreSQL 16 database
/// (slice S1.02 items 3 and 4). Permission checking and data scopes are test doubles here because their real
/// implementations arrive in S1.05/S1.06; the endpoints are not mapped by the API host until S1.03.
/// </summary>
public sealed class GeneratedEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _server = new();
    private readonly TestPermissions _permissions = new();
    private ScratchDatabase? _database;
    private WebApplication? _app;
    private NpgsqlDataSource? _numbers;
    private HttpClient? _client;

    private HttpClient Client => _client!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _server.InitializeAsync();
        _database = await ScratchDatabase.CreateAsync(_server.ConnectionString, "erp_it_api_" + Guid.NewGuid().ToString("N")[..8], ct);
        await SqlScriptRunner.ApplyAsync(_database.ConnectionString, MigratedDatabases.TenantScripts, ct);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<KernelDbContext>(o => o.UseNpgsql(_database.ConnectionString));
        builder.Services.AddDbContext<HrmDbContext>(o => o.UseNpgsql(_database.ConnectionString));
        builder.Services.AddKernelGeneratedRepositories().AddHrmGeneratedRepositories();
        builder.Services.AddSingleton<IClock>(new TestClock(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero)));
        builder.Services.AddSingleton<ICurrentActor>(new TestActor(Guid.CreateVersion7()));
        builder.Services.AddSingleton<IDataScopeFilter, AllowAllAccess.AllRows>();
        builder.Services.AddSingleton<IFieldAccess, AllowAllAccess.AllFields>();
        builder.Services.AddSingleton<IPermissionChecker>(_permissions);
        builder.Services.AddSingleton<ISensitiveAccessLog>(new RecordingAccessLog()); // S1.07: generated reads log sensitive views
        // S1.08: generated creates of numbered tables take their number from the kernel numbering service.
        _numbers = NpgsqlDataSource.Create(_database.ConnectionString);
        builder.Services.AddSingleton<global::Erp.BuildingBlocks.Application.Tenancy.ITenantConnectionProvider>(new FixedTenant(_numbers));
        builder.Services.AddSingleton(new global::Erp.Kernel.Infrastructure.Numbering.DocumentTypeRegistry([new HrmNumberingTypes()]));
        builder.Services.AddScoped<global::Erp.Kernel.Contracts.Numbering.INumberAllocator, global::Erp.Kernel.Infrastructure.Numbering.NumberAllocator>();
        _app = builder.Build();
        _app.MapKernelGeneratedEndpoints();
        _app.MapHrmGeneratedEndpoints();
        await _app.StartAsync(ct);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        if (_numbers is not null)
        {
            await _numbers.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DropAsync(CancellationToken.None);
        }

        await _server.DisposeAsync();
    }

    private List<(RouteEndpoint Endpoint, GeneratedEndpointMetadata Metadata)> GeneratedEndpoints() =>
        _app!.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => (e, e.Metadata.GetMetadata<GeneratedEndpointMetadata>()))
            .Where(x => x.Item2 is not null)
            .Select(x => (x.e, x.Item2!))
            .ToList();

    [Fact]
    public async Task S102_every_generated_endpoint_refuses_a_caller_without_its_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissions.Granted.Clear();
        var endpoints = GeneratedEndpoints();
        Assert.True(endpoints.Count > 400, $"expected the generated endpoints to be mapped, found {endpoints.Count}");

        var failures = new List<string>();
        foreach (var (endpoint, metadata) in endpoints)
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0];
            var path = endpoint.RoutePattern.RawText!.Replace("{id:guid}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT")
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await Client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{method} {path} ({metadata.Permission}) returned {(int)response.StatusCode}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void S102_every_endpoint_permission_is_in_the_module_permission_catalogue()
    {
        var catalogue = KernelPermissions.All.Concat(HrmPermissions.All).Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var missing = GeneratedEndpoints().Select(e => e.Metadata.Permission).Where(p => !catalogue.Contains(p)).Distinct().ToList();
        Assert.Empty(missing);
        Assert.All(catalogue, code => Assert.Matches(@"^[a-z]+\.[a-z_]+\.(read|create|update|delete)$", code));
    }

    [Fact]
    public void S102_no_endpoint_is_generated_for_secret_or_internal_tables()
    {
        var resources = GeneratedEndpoints().Select(e => e.Metadata.Resource).ToHashSet(StringComparer.Ordinal);
        string[] excluded = ["iam.user_credentials", "iam.password_history", "iam.mfa_devices", "iam.sessions", "iam.api_clients", "aud.change_log", "aud.access_log", "aud.subject_keys", "meta.schema_history", "cfg.numbering_counters", "hrm.outbox_messages"];
        Assert.All(excluded, r => Assert.DoesNotContain(r, resources));
        Assert.DoesNotContain(resources, r => r.StartsWith("cp.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S102_master_data_round_trip_with_concurrency_check_and_soft_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissions.GrantAll("org.branches");
        var companyId = await CreateCompanyAsync(ct);

        var created = await Client.PostAsJsonAsync("/api/org/branches", new { companyId, code = "KHI", nameI18n = new { en = "Karachi", ur = "کراچی" } }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var branch = await created.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var id = branch.GetProperty("id").GetGuid();
        Assert.Equal(7, id.Version); // UUIDv7 from the server, never from the client
        Assert.Equal(1, branch.GetProperty("rowVersion").GetInt64());
        Assert.True(branch.GetProperty("isActive").GetBoolean());

        var duplicate = await Client.PostAsJsonAsync("/api/org/branches", new { companyId, code = "khi", nameI18n = new { en = "Again" } }, ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode); // unique (company_id, lower(code))

        var missing = await Client.PostAsJsonAsync("/api/org/branches", new { companyId }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var updated = await Client.PutAsJsonAsync($"/api/org/branches/{id}", new { code = "KHI", nameI18n = new { en = "Karachi Head Office" }, rowVersion = 1 }, ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("rowVersion").GetInt64());

        var stale = await Client.PutAsJsonAsync($"/api/org/branches/{id}", new { code = "KHI", nameI18n = new { en = "Lost update" }, rowVersion = 1 }, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Rule SC-2 (S1.03): the company also has the default branch created with it, so it lists two branches.
        var list = await Client.GetFromJsonAsync<JsonElement>($"/api/org/branches?companyId={companyId}&limit=10", Json, ct);
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
        Assert.Single(list.GetProperty("items").EnumerateArray(), b => b.GetProperty("id").GetGuid() == id);

        var deleted = await Client.DeleteAsync($"/api/org/branches/{id}?rowVersion=2", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/org/branches/{id}", ct)).StatusCode);

        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("select is_deleted, row_version from org.branches where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.True(reader.GetBoolean(0)); // soft delete keeps the row
        Assert.Equal(3, reader.GetInt64(1));
    }

    [Fact]
    public async Task S102_append_only_tables_have_no_update_or_delete_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissions.GrantAll("hrm.leave_ledger");
        var appendOnly = MigratedDatabases.Catalog.Tables.Where(t => t.Traits.Contains("app") && t.HasApi).Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(appendOnly);
        var writes = GeneratedEndpoints().Where(e => appendOnly.Contains(e.Metadata.Resource) && e.Metadata.Action is "update" or "delete").ToList();
        Assert.Empty(writes);

        var put = await Client.PutAsJsonAsync($"/api/hrm/leave-ledger/{Guid.CreateVersion7()}", new { }, ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
    }

    [Fact]
    public async Task S102_list_pages_with_a_keyset_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissions.GrantAll("org.branches");
        var companyId = await CreateCompanyAsync(ct);
        for (var i = 0; i < 5; i++)
        {
            var response = await Client.PostAsJsonAsync("/api/org/branches", new { companyId, code = "B" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), nameI18n = new { en = "Branch" } }, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var seen = new List<Guid>();
        string? after = null;
        do
        {
            var page = await Client.GetFromJsonAsync<JsonElement>($"/api/org/branches?companyId={companyId}&limit=2" + (after is null ? string.Empty : "&after=" + after), Json, ct);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
            after = page.GetProperty("next").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next").GetString();
        }
        while (after is not null);

        // Five created here plus the default branch every company gets (rule SC-2, S1.03).
        Assert.Equal(6, seen.Distinct().Count());
        Assert.Equal(6, seen.Count);
    }

    private async Task<Guid> CreateCompanyAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await new RowFactory(connection, transaction, MigratedDatabases.Catalog)
            .InsertAsync(MigratedDatabases.Catalog.Tables.Single(t => t.FullName == "org.companies"), null, ct);
        await transaction.CommitAsync(ct);
        return (Guid)row["id"]!;
    }

    private sealed class TestPermissions : IPermissionChecker
    {
        public HashSet<string> Granted { get; } = new(StringComparer.Ordinal);

        public void GrantAll(string resource)
        {
            foreach (var action in new[] { "read", "create", "update", "delete" })
            {
                Granted.Add(resource + "." + action);
            }
        }

        public ValueTask<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) => ValueTask.FromResult(Granted.Contains(permission));
    }

    private sealed record TestActor(Guid UserId) : ICurrentActor;

    private sealed class FixedTenant(NpgsqlDataSource source) : global::Erp.BuildingBlocks.Application.Tenancy.ITenantConnectionProvider
    {
        public System.Data.Common.DbDataSource DataSource => source;
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
