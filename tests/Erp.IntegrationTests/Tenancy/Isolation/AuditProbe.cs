using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;
using Npgsql;
using NpgsqlTypes;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the audit endpoints (slice S1.07), the counterpart of <see cref="CrossTenantProbe"/>. Each
/// customer gets fresh audit material written through the audit functions (a role with a change-log entry, an access-log
/// row and a security event, each carrying a unique marker). Then:
/// <list type="number">
/// <item>B's host and token aimed at A's ids (path and query), with and without headers naming A: one record's history is
/// 404; searches, lists, export, own sign-ins and the chain check never contain A's markers; never 5xx.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows A's markers come back for A.</item>
/// </list>
/// </summary>
public sealed class AuditProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid RoleId, Guid UserRef, string Marker);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);
        var markers = new[] { a.RoleId.ToString(), a.UserRef.ToString(), a.Marker };
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
            else if (endpoint.Kind == EndpointKind.AuditRecord && reply.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: expected 404 for A's record, got {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind != EndpointKind.AuditRecord && reply.Status != HttpStatusCode.OK)
            {
                failures.Add($"{endpoint} [{label}]: B's own call was refused ({(int)reply.Status}); the path was not exercised. {Short(reply.Body)}");
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

        // Positive control: the same call on A's own host returns A's material (verify answers for A's chain).
        var own = await SendAsync(fx.HostA, tokenA, method, path, body, hints: false, ct);
        if (own.Status != HttpStatusCode.OK)
        {
            failures.Add($"{endpoint}: positive control failed: A was not served on its own host ({(int)own.Status}). {Short(own.Body)}");
        }
        else if (endpoint.Kind != EndpointKind.AuditVerify && !own.Body.Contains(a.Marker, StringComparison.Ordinal))
        {
            failures.Add($"{endpoint}: positive control failed: A's answer does not contain A's marker {a.Marker}. {Short(own.Body)}");
        }

        _ = b;
        return failures;
    }

    /// <summary>Path (A's ids in route and query) and body of one audit endpoint.</summary>
    private static (string Path, string? Body) Target(ApiEndpoint endpoint, Material m)
    {
        var key = endpoint.Method + " " + endpoint.Pattern.TrimEnd('/');
        return key switch
        {
            "GET /api/aud/changes" => ($"/api/aud/changes?entityType=iam.roles&entityId={m.RoleId}", null),
            "GET /api/aud/export" => ($"/api/aud/export?entityType=iam.roles&entityId={m.RoleId}", null),
            "GET /api/aud/history/{entityType}/{id:guid}" => ($"/api/aud/history/iam.roles/{m.RoleId}", null),
            "GET /api/aud/access-log" => ($"/api/aud/access-log?userId={m.UserRef}", null),
            "GET /api/aud/security-events" => ($"/api/aud/security-events?subject={m.Marker}", null),
            "GET /api/aud/my-sign-ins" => ("/api/aud/my-sign-ins", null),
            "POST /api/aud/verify" => ("/api/aud/verify", JsonSerializer.Serialize(new { from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), to = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) })),
            _ => throw new InvalidOperationException($"No probe target for audit endpoint {key}; extend AuditProbe."),
        };
    }

    /// <summary>Fresh audit material in the customer's database, written through the audit functions as the owner.</summary>
    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var marker = "probe" + Guid.NewGuid().ToString("N")[..12];
        var role = Guid.CreateVersion7();
        var userRef = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Probe role"}', 'tenant', now(), $3, now(), $3)
            """, ct, role, "probe_audit_" + marker, system);
        var changes = JsonSerializer.Serialize(new[] { new { id = Guid.CreateVersion7(now), occurred_at = now.ToString("O"), actor_kind = "user", actor_user_id = system,
            entity_type = "iam.roles", entity_id = role, action = "create", changes = new[] { new { field = "code", after = marker } }, source = "ui", created_by = system } });
        await ExecAsync(c, "select aud.append_changes($1::jsonb)", ct, changes);
        await ExecAsync(c, "select aud.append_access($1, $2, $3, 'hrm.pay_results', null, $4, 'view', 1, null, $3)", ct, Guid.CreateVersion7(now), now, userRef, new[] { marker });
        await ExecAsync(c, "select aud.append_security_event($1, $2, 'iam.sign_in_succeeded', $3, $4, 'info', $5::jsonb, $6)", ct,
            Guid.CreateVersion7(now), now, system.ToString(), marker, JsonSerializer.Serialize(new { outcome = "success", marker }), system);
        return new Material(role, userRef, marker);
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
            command.Parameters.Add(value is string[] texts ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = texts } : new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);
}
