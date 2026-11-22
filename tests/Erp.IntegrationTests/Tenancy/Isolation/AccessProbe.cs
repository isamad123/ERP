using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the access-control endpoints (slice S1.06), the counterpart of <see cref="CrossTenantProbe"/>
/// for routes that act on roles, assignments, groups and maker-checker requests. Each customer gets fresh material (two
/// roles, a user with a role, a group with a member and a role, a pending request). Then:
/// <list type="number">
/// <item>B's host and token aimed at A's ids (path, query, body), with and without headers naming A: actions on a record
/// are 404; creates and queries never answer with A's data; the catalogue never lists A's roles; never 5xx.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class AccessProbe(IsolationFixture fx, WebApplication app)
{
    private const string Resource = "org.companies";

    private sealed record Material(
        Guid RoleId, Guid Role2Id, string RoleCode, long RoleVersion, Guid UserId, Guid User2Id, Guid GroupId, long GroupVersion,
        Guid AssignmentId, Guid GroupRoleId, Guid RequestId, Guid ScopeId);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        await MaterialAsync(fx.CodeB, ct); // B's own material, so B's calls have real rows of their own to hit
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);
        var markers = new[] { a.RoleId, a.Role2Id, a.UserId, a.User2Id, a.GroupId, a.AssignmentId, a.GroupRoleId, a.RequestId }.Select(g => g.ToString()).Append(a.RoleCode).ToList();
        var method = new HttpMethod(endpoint.Method);
        var (path, body) = Target(endpoint, a);
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var reply = await SendAsync(fx.HostB, tokenB, method, path, body, hints, ct);
            if ((int)reply.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.AccessRecordAction && reply.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: expected 404 for A's record, got {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.AccessCatalog && reply.Status != HttpStatusCode.OK)
            {
                failures.Add($"{endpoint} [{label}]: B's own catalogue was refused ({(int)reply.Status}); the path was not exercised. {Short(reply.Body)}");
            }

            var leaked = markers.Where(m => reply.Body.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
            if (leaked.Count > 0)
            {
                failures.Add($"{endpoint} [{label}]: LEAK: the response contains customer A's data {string.Join(", ", leaked)}. {Short(reply.Body)}");
            }
        }

        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var (label, host, token, status) in new (string, string, string?, HttpStatusCode)[]
        {
            ("A host + B token", fx.HostA, tokenB, HttpStatusCode.Unauthorized),
            ("B host + A token", fx.HostB, tokenA, HttpStatusCode.Unauthorized),
            ("A host + no token", fx.HostA, null, HttpStatusCode.Unauthorized),
            ("A host + token signed by an untrusted key", fx.HostA, fx.Tenancy.Token(fx.CodeA, key: forger), HttpStatusCode.Unauthorized),
            ("unknown host + A token", "unknown-" + fx.Tenancy.Suffix + ".erp.test", tokenA, HttpStatusCode.NotFound),
        })
        {
            var reply = await SendAsync(host, token, method, path, body, hints: false, ct);
            if (reply.Status != status)
            {
                failures.Add($"{endpoint} [{label}]: expected {(int)status}, got {(int)reply.Status}. {Short(reply.Body)}");
            }
        }

        var changed = IsolationFixture.Changed(before, await fx.FingerprintAsync(fx.CodeA, ct));
        if (changed.Count > 0)
        {
            failures.Add($"{endpoint}: customer A's database changed during the cross-customer calls: {string.Join(", ", changed)}");
        }

        // Positive control: the same call works for A on A's host (B's material keeps the two runs apart).
        var own = await SendAsync(fx.HostA, tokenA, method, path, body, hints: false, ct);
        if ((int)own.Status is < 200 or >= 300)
        {
            failures.Add($"{endpoint}: positive control failed: A was not served on its own host ({(int)own.Status}). {Short(own.Body)}");
        }
        else if (endpoint.Kind == EndpointKind.AccessCatalog && endpoint.Resource == "iam.maker_checker_requests")
        {
            // S1.12d: the pending-changes list names A's pending request.
            if (!own.Body.Contains(a.RequestId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{endpoint}: positive control failed: A's pending changes do not list A's request {a.RequestId}.");
            }
        }
        else if (endpoint.Kind == EndpointKind.AccessCatalog && !own.Body.Contains(a.RoleCode, StringComparison.Ordinal))
        {
            failures.Add($"{endpoint}: positive control failed: A's catalogue does not list A's role {a.RoleCode}.");
        }

        return failures;
    }

    /// <summary>Path (A's ids in route and query) and body (naming A's ids) of one endpoint.</summary>
    private static (string Path, string? Body) Target(ApiEndpoint endpoint, Material m)
    {
        var id = endpoint.Resource switch
        {
            "iam.roles" => m.RoleId,
            "iam.user_roles" => m.AssignmentId,
            "iam.user_groups" => m.GroupId,
            "iam.user_group_roles" => m.GroupRoleId,
            "iam.maker_checker_requests" => m.RequestId,
            "iam.users" => m.UserId, // S1.13a: the user record's views and single revocations
            _ => Guid.Empty,
        };
        var path = endpoint.Pattern.Replace("{id:guid}", id.ToString(), StringComparison.Ordinal).Replace("{userId:guid}", m.UserId.ToString(), StringComparison.Ordinal)
            .Replace("{sessionId:guid}", Guid.NewGuid().ToString(), StringComparison.Ordinal).Replace("{deviceId:guid}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
        var unique = Guid.NewGuid().ToString("N")[..10];
        var key = endpoint.Method + " " + endpoint.Pattern.TrimEnd('/');
        return key switch
        {
            "GET /api/iam/roles/compare" => ($"{path}?left={m.RoleId}&right={m.Role2Id}", null),
            "GET /api/iam/roles/{id:guid}/field-preview" => ($"{path}?resource={Resource}", null),
            "GET /api/iam/data-scope-preview" => ($"{path}?userId={m.UserId}&resource={Resource}", null),
            _ when endpoint.Method == "GET" => (path, null),
            "POST /api/iam/roles" => (path, Json(new { code = "probe_new_" + unique, name = new { en = "Probe" }, copyFromRoleId = m.RoleId })),
            "PUT /api/iam/roles/{id:guid}" => (path, Json(new { name = new { en = "Probe renamed" }, rowVersion = m.RoleVersion })),
            "PUT /api/iam/roles/{id:guid}/permissions" => (path, Json(new { permissions = new[] { Resource + ".read" }, reason = "probe" })),
            "PUT /api/iam/roles/{id:guid}/data-scopes" => (path, Json(new { scopes = new[] { new { resource = Resource, dataScopeId = m.ScopeId } }, reason = "probe" })),
            "PUT /api/iam/roles/{id:guid}/field-policies" => (path, Json(new { fields = new[] { new { resource = Resource, field = "legal_name", access = "read_only" } }, reason = "probe" })),
            "POST /api/iam/user-roles" => (path, Json(new { userId = m.UserId, roleId = m.Role2Id, reason = "probe" })),
            "POST /api/iam/user-groups" => (path, Json(new { code = "probe_g_" + unique, name = new { en = "Probe group" } })),
            "PUT /api/iam/user-groups/{id:guid}" => (path, Json(new { name = new { en = "Probe group renamed" }, rowVersion = m.GroupVersion })),
            "POST /api/iam/user-groups/{id:guid}/members" => (path, Json(new { userId = m.User2Id })),
            "POST /api/iam/user-groups/{id:guid}/members/{userId:guid}/remove" => (path, null),
            "POST /api/iam/user-groups/{id:guid}/roles" => (path, Json(new { roleId = m.Role2Id, reason = "probe" })),
            "POST /api/iam/data-scopes" => (path, Json(new { code = "probe_s_" + unique, name = new { en = "Probe scope" }, kind = "own", definition = new { } })),
            "PUT /api/iam/settings/{key}" => (path.Replace("{key}", "iam.session.idle_minutes", StringComparison.Ordinal), Json(new { value = 30, reason = "probe" })),
            _ when endpoint.Kind == EndpointKind.AccessRecordAction => (path, Json(new { reason = "probe" })),
            _ => throw new InvalidOperationException($"No probe target for access endpoint {key}; extend AccessProbe."),
        };
    }

    /// <summary>Fresh access-control material in the customer's database, written as the database owner would.</summary>
    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var n = Guid.NewGuid().ToString("N")[..10];
        var (role, role2, user, user2, group) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (assignment, groupRole, request) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $3, $3 || '@probe.test', $3, 'en', 'Asia/Karachi', false, 'active', now(), $4, now(), $4),
                   ($2, $3 || 'b', $3 || 'b@probe.test', $3, 'en', 'Asia/Karachi', false, 'active', now(), $4, now(), $4)
            """, ct, user, user2, "ap" + n, system);
        await ExecAsync(c, """
            insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by)
            values ($1, $3, '{"en":"Probe role"}', 'tenant', now(), $4, now(), $4), ($2, $3 || '_b', '{"en":"Probe role B"}', 'tenant', now(), $4, now(), $4)
            """, ct, role, role2, "probe_role_" + n, system);
        await ExecAsync(c, "insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = 'org.companies.read'", ct, role);
        await ExecAsync(c, "insert into iam.user_groups (id, code, name_i18n, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Probe group\"}', now(), $3, now(), $3)", ct, group, "probe_group_" + n, system);
        await ExecAsync(c, "insert into iam.user_group_members (group_id, user_id) values ($1, $2)", ct, group, user);
        await ExecAsync(c, """
            insert into iam.user_roles (id, user_id, role_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 'probe', $4, now(), $5, now(), $5)
            """, ct, assignment, user, role, today.AddDays(-1), system);
        await ExecAsync(c, """
            insert into iam.user_group_roles (id, group_id, role_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 'probe', $4, now(), $5, now(), $5)
            """, ct, groupRole, group, role, today.AddDays(-1), system);
        await ExecAsync(c, """
            insert into iam.maker_checker_requests (id, action_type, subject_type, subject_id, payload, requested_by, status, created_at, created_by, updated_at, updated_by)
            values ($1, 'iam.role.set_permissions', 'iam.roles', $2, jsonb_build_object('kind', 'iam.role.set_permissions', 'roleId', $2::text, 'permissions', '[]'::jsonb), $3, 'pending', now(), $3, now(), $3)
            """, ct, request, role2, user);
        var scope = await ScalarAsync<Guid>(c, "select id from iam.data_scopes where code = 'all'", ct);
        return new Material(role, role2, "probe_role_" + n, 1, user, user2, group, 1, assignment, groupRole, request, scope);
    }

    private async Task<Reply> SendAsync(string host, string? token, HttpMethod method, string pathAndQuery, string? body, bool hints, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(app, host, token);
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var target = hints ? pathAndQuery + separator + "tenant=" + fx.CodeA + "&tenantCode=" + fx.CodeA : pathAndQuery;
        using var request = new HttpRequestMessage(method, target);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (hints)
        {
            request.Headers.Add("X-Tenant", fx.CodeA);
            request.Headers.Add("X-Tenant-Code", fx.CodeA);
            request.Headers.Add("X-Forwarded-Host", fx.HostA);
            request.Headers.Add("Forwarded", "host=" + fx.HostA);
        }

        using var response = await client.SendAsync(request, ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);
}
