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
/// The cross-customer probe of the notification endpoints (slice S1.11b1). Each customer gets fresh material: an active
/// administrator with a notification item, an audience rule and a customer text. Then, as for the other probes:
/// <list type="number">
/// <item>B's host and token aimed at A's ids, with and without headers naming A: actions on A's job or run are 404; lists
/// and health answer B's own data only; never 5xx; nothing of A's in any answer.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class NotificationProbe(IsolationFixture fx, WebApplication app)
{
    private static readonly string[] AdminRole = ["tenant_admin"];
    private static readonly string[] InApp = ["in_app"];

    private sealed record Material(Guid Admin, Guid Item, Guid Rule, long RuleVersion, Guid Message);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var markers = new[] { a.Admin, a.Item, a.Rule, a.Message }.Select(g => g.ToString()).ToArray();
        var method = new HttpMethod(endpoint.Method);
        var (path, body) = Target(endpoint, a);
        var tokenA = fx.Tenancy.Token(fx.CodeA, user: a.Admin);
        var tokenB = fx.Tenancy.Token(fx.CodeB, user: b.Admin);
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var reply = await SendAsync(fx.HostB, tokenB, method, path, body, hints, ct);
            if ((int)reply.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.NotificationRecordAction && reply.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: expected 404 for A's job or run, got {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.NotificationCatalog && (int)reply.Status is < 200 or >= 300)
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

    private static (string Path, string? Body) Target(ApiEndpoint endpoint, Material m)
    {
        var key = endpoint.Method + " " + endpoint.Pattern;
        return key switch
        {
            "GET /api/ntf/centre/items" or "GET /api/ntf/centre/unread-count" or "GET /api/ntf/centre/preferences" or "GET /api/ntf/catalogue" or "GET /api/ntf/text-templates" or "GET /api/ntf/rules"
                or "GET /api/ntf/deliveries" or "GET /api/ntf/deliveries/summary" => (endpoint.Pattern, null),
            "GET /api/ntf/deliveries/{id:guid}" => ($"/api/ntf/deliveries/{m.Message}", null),
            "POST /api/ntf/deliveries/{id:guid}/resend" => ($"/api/ntf/deliveries/{m.Message}/resend", "{}"),
            "POST /api/ntf/text-templates/test-send" => (endpoint.Pattern, Json(new { eventCode = "workflow.task_assigned", language = "en", subject = "Probe", body = "Probe text" })),
            "POST /api/ntf/centre/items/read-all" => (endpoint.Pattern, "{}"),
            "PUT /api/ntf/centre/preferences" => (endpoint.Pattern, Json(new { preferences = new[] { new { eventGroup = "approvals", channel = "email", enabled = true } } })),
            "POST /api/ntf/centre/items/{id:guid}/read" => ($"/api/ntf/centre/items/{m.Item}/read", "{}"),
            "POST /api/ntf/centre/items/{id:guid}/unread" => ($"/api/ntf/centre/items/{m.Item}/unread", "{}"),
            "POST /api/ntf/text-templates" => (endpoint.Pattern, Json(new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en", subject = "Probe", body = "Probe text" })),
            "POST /api/ntf/text-templates/restore" => (endpoint.Pattern, Json(new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en" })),
            "POST /api/ntf/text-templates/preview" => (endpoint.Pattern, Json(new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en", subject = "Probe", body = "Probe text" })),
            "POST /api/ntf/rules" => (endpoint.Pattern, Json(new { eventCode = "workflow.task_assigned", audience = new { roles = AdminRole }, channels = InApp, mandatory = false })),
            "PUT /api/ntf/rules/{id:guid}" => ($"/api/ntf/rules/{m.Rule}", Json(new { eventCode = "workflow.task_assigned", audience = new { roles = AdminRole }, channels = InApp, mandatory = false, rowVersion = m.RuleVersion })),
            "DELETE /api/ntf/rules/{id:guid}" => ($"/api/ntf/rules/{m.Rule}?rowVersion={m.RuleVersion}", null),
            _ => throw new InvalidOperationException($"No probe target for notification endpoint {key}; extend NotificationProbe."),
        };
    }

    /// <summary>Fresh material in one customer, written as the database owner would.</summary>
    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        await using var c = await fx.OpenAdminAsync(code, ct);
        var admin = Guid.CreateVersion7();
        var name = "ntp" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, ct, admin, name, system);
        await ExecAsync(c, """
            insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
            select $1, $2, r.id, null, 'probe', current_date - 1, now(), $3, now(), $3 from iam.roles r where r.code = 'tenant_admin'
            """, ct, Guid.CreateVersion7(), admin, system);
        var (item, rule) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecAsync(c, """
            insert into ntf.in_app_items (id, user_id, event_code, title, body, event_group, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'workflow.task_assigned', 'Probe item', 'Probe body', 'approvals', now(), $3, now(), $3)
            """, ct, item, admin, system);
        await ExecAsync(c, """
            insert into ntf.subscriptions (id, event_code, audience, channels, is_mandatory, name, created_at, created_by, updated_at, updated_by)
            values ($1, 'workflow.task_assigned', '{"roles":["tenant_admin"]}', '{in_app}', false, 'Probe rule', now(), $2, now(), $2)
            """, ct, rule, system);
        // A finished e-mail of A's delivery log (S1.11b2), so resend and detail have a record to aim at.
        var message = Guid.CreateVersion7();
        await ExecAsync(c, """
            insert into ntf.outbound_messages (id, created_at, channel, recipient, payload, attempts, status, user_id, event_code, created_by, updated_at, updated_by)
            values ($1, now(), 'email', $2 || '@probe.test', '{"subject":"Probe mail","body":"Probe","language":"en"}', 1, 'failed', $3, 'workflow.task_assigned', $4, now(), $4)
            """, ct, message, name, admin, system);
        return new Material(admin, item, rule, 1, message);
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
