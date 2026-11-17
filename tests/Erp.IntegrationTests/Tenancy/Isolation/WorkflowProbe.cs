using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Workflow;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the workflow endpoints (slice S1.10a). Each customer gets fresh material: two active
/// administrators and a requester, the test module's routes, a draft version, and a running request whose task is
/// assigned to the first administrator (waiting for the requester's answer when the answer endpoint is probed). Then, as for the other probes:
/// <list type="number">
/// <item>B's host and token aimed at A's ids (route and body), with and without headers naming A: actions on A's route,
/// version, task or request are 404; lists answer B's own data only; never 5xx.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host (as A's administrator, or as A's requester for
/// the requester's endpoints) shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class WorkflowProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid Admin, Guid OtherAdmin, Guid Requester, Guid Definition, Guid Draft, Guid Instance, Guid Task);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var question = endpoint.Pattern.EndsWith("/answer", StringComparison.Ordinal); // the requester answers only a question asked
        var a = await MaterialAsync(fx.CodeA, question, ct);
        var b = await MaterialAsync(fx.CodeB, question, ct);
        var markers = new[] { a.Admin, a.OtherAdmin, a.Requester, a.Definition, a.Draft, a.Instance, a.Task }.Select(g => g.ToString()).ToArray();
        var method = new HttpMethod(endpoint.Method);
        var (path, body, asRequester) = Target(endpoint, a);
        var tokenA = fx.Tenancy.Token(fx.CodeA, user: asRequester ? a.Requester : a.Admin);
        var tokenB = fx.Tenancy.Token(fx.CodeB, user: asRequester ? b.Requester : b.Admin);
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var reply = await SendAsync(fx.HostB, tokenB, method, path, body, hints, ct);
            if ((int)reply.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.WorkflowRecordAction && reply.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: expected 404 for A's record, got {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.WorkflowCatalog && reply.Status != HttpStatusCode.OK)
            {
                failures.Add($"{endpoint} [{label}]: B's own list was refused ({(int)reply.Status}); the path was not exercised. {Short(reply.Body)}");
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
            ("A host + token signed by an untrusted key", fx.HostA, fx.Tenancy.Token(fx.CodeA, key: forger, user: a.Admin), HttpStatusCode.Unauthorized),
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

        var own = await SendAsync(fx.HostA, tokenA, method, path, body, hints: false, ct);
        if ((int)own.Status is < 200 or >= 300)
        {
            failures.Add($"{endpoint}: positive control failed: A was not served on its own host ({(int)own.Status}). {Short(own.Body)}");
        }

        return failures;
    }

    /// <summary>The call aimed at A's material, and whether A's requester (not A's administrator) makes it.</summary>
    private static (string Path, string? Body, bool AsRequester) Target(ApiEndpoint endpoint, Material m)
    {
        var key = endpoint.Method + " " + endpoint.Pattern;
        var route = """{"steps": [{"key": "probe", "name": {"en": "Probe", "ur": "جانچ"}, "approver": {"kind": "user", "user_id": "ADMIN"}}]}""".Replace("ADMIN", m.Admin.ToString(), StringComparison.Ordinal);
        return key switch
        {
            "GET /api/wf/document-types" or "GET /api/wf/definitions" or "GET /api/wf/inbox" => (endpoint.Pattern, null, false),
            "GET /api/wf/definitions/{id:guid}" => ($"/api/wf/definitions/{m.Definition}", null, false),
            "POST /api/wf/definitions/{id:guid}/versions" => ($"/api/wf/definitions/{m.Definition}/versions", $$"""{"route": {{route}}}""", false),
            "POST /api/wf/definitions/{id:guid}/deactivate" => ($"/api/wf/definitions/{m.Definition}/deactivate", Json(new { reason = "probe", rowVersion = 1 }), false),
            "PUT /api/wf/versions/{id:guid}" => ($"/api/wf/versions/{m.Draft}", $$"""{"rowVersion": 1, "route": {{route}}}""", false),
            "POST /api/wf/versions/{id:guid}/check" => ($"/api/wf/versions/{m.Draft}/check", "{}", false),
            "POST /api/wf/versions/{id:guid}/activate" => ($"/api/wf/versions/{m.Draft}/activate", Json(new { rowVersion = 1 }), false),
            "GET /api/wf/tasks/{id:guid}" => ($"/api/wf/tasks/{m.Task}", null, false),
            "POST /api/wf/tasks/{id:guid}/approve" or "POST /api/wf/tasks/{id:guid}/reject" or "POST /api/wf/tasks/{id:guid}/return"
                => (endpoint.Pattern.Replace("{id:guid}", m.Task.ToString(), StringComparison.Ordinal), Json(new { comment = "probe" }), false),
            "POST /api/wf/tasks/{id:guid}/reassign" => ($"/api/wf/tasks/{m.Task}/reassign", Json(new { toUserId = m.OtherAdmin, reason = "probe" }), false),
            "POST /api/wf/tasks/{id:guid}/ask" => ($"/api/wf/tasks/{m.Task}/ask", Json(new { question = "probe" }), false),
            "POST /api/wf/instances/{id:guid}/answer" => ($"/api/wf/instances/{m.Instance}/answer", Json(new { answer = "probe" }), true),
            "POST /api/wf/instances/{id:guid}/withdraw" => ($"/api/wf/instances/{m.Instance}/withdraw", Json(new { reason = "probe" }), true),
            _ => throw new InvalidOperationException($"No probe target for workflow endpoint {key}; extend WorkflowProbe."),
        };
    }

    /// <summary>
    /// Fresh material in one customer, written as the database owner would: the test module's routes, an active
    /// expense route (definition row version 1 by the time a probe deactivates it), a draft, and a running request.
    /// </summary>
    private async Task<Material> MaterialAsync(string code, bool question, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        await using var c = await fx.OpenAdminAsync(code, ct);
        await WorkflowSetup.EnsureAsync(c, null, WorkflowRig.Types, DateTimeOffset.UtcNow, ct);
        var (admin, other, requester) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        foreach (var (user, role) in new[] { (admin, "tenant_admin"), (other, "tenant_admin"), (requester, "business_user") })
        {
            var name = "wfp" + Guid.NewGuid().ToString("N")[..12];
            await ExecAsync(c, """
                insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
                """, ct, user, name, system);
            await ExecAsync(c, """
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, $2, r.id, null, 'probe', current_date - 1, now(), $3, now(), $3 from iam.roles r where r.code = $4
                """, ct, Guid.CreateVersion7(), user, system, role);
        }

        // A fresh route for the expense type, so the definition's row version is known (1) and its active version names this admin.
        var (definition, version, draft) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var route = """{"steps": [{"key": "probe", "name": {"en": "Probe", "ur": "جانچ"}, "approver": {"kind": "user", "user_id": "ADMIN"}, "bulk_approval": false}]}""".Replace("ADMIN", admin.ToString(), StringComparison.Ordinal);
        await ExecAsync(c, "update wf.workflow_definitions set code = code || '-' || right(id::text, 12), document_type = document_type || '-old' where document_type = $1", ct, WorkflowRig.Expense);
        await ExecAsync(c, """
            insert into wf.workflow_definitions (id, code, name_i18n, document_type, status, is_active, requires_approval, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, '{"en":"Expense","ur":"اخراجات"}', $2, 'active', true, false, now(), $3, now(), $3, 1)
            """, ct, definition, WorkflowRig.Expense, system);
        await ExecAsync(c, """
            insert into wf.workflow_versions (id, definition_id, version, definition, status, activated_at, activated_by, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 1, $3::jsonb, 'active', now(), $4, now(), $4, now(), $4), ($5, $2, 2, $3::jsonb, 'draft', null, null, now(), $4, now(), $4)
            """, ct, version, definition, route, system, draft);
        await ExecAsync(c, "update wf.workflow_definitions set active_version_id = $2 where id = $1", ct, definition, version);

        var company = await ScalarAsync<Guid>(c, "select id from org.companies where not is_deleted order by created_at limit 1", ct);
        var (instance, task) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecAsync(c, """
            insert into wf.workflow_instances (id, company_id, version_id, document_type, document_id, requester_user_id, started_at, context, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, now(), '{"values": {}}', 'running', now(), $6, now(), $6)
            """, ct, instance, company, version, WorkflowRig.Expense, Guid.CreateVersion7(), requester);
        await ExecAsync(c, """
            insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, status, info_requested_at, info_requested_by, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'probe', $3, 'pending', case when $5 then now() end, case when $5 then $3 end, now(), $4, now(), $4)
            """, ct, task, instance, admin, requester, question);
        return new Material(admin, other, requester, definition, draft, instance, task);
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
