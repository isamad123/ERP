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
/// The cross-customer probe of the job-centre endpoints (slice S1.11a). Each customer gets fresh material: an active
/// administrator, the id and row version of its <c>kernel.purge</c> job (editable, manual), a finished run (for rerun and
/// detail) and a queued run (for cancel). Then, as for the other probes:
/// <list type="number">
/// <item>B's host and token aimed at A's ids, with and without headers naming A: actions on A's job or run are 404; lists
/// and health answer B's own data only; never 5xx; nothing of A's in any answer.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class JobProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid Admin, Guid Job, long JobVersion, Guid Finished, Guid Queued);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var markers = new[] { a.Admin, a.Job, a.Finished, a.Queued }.Select(g => g.ToString()).ToArray();
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
            else if (endpoint.Kind == EndpointKind.JobRecordAction && reply.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: expected 404 for A's job or run, got {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.JobCatalog && reply.Status != HttpStatusCode.OK)
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
            "GET /api/jobs/definitions" or "GET /api/jobs/runs" or "GET /api/jobs/health" or "GET /api/jobs/system-health" => (endpoint.Pattern, null),
            "PUT /api/jobs/definitions/{id:guid}/schedule" => ($"/api/jobs/definitions/{m.Job}/schedule", Json(new { schedule = (object?)null, isEnabled = true, rowVersion = m.JobVersion })),
            "POST /api/jobs/definitions/{id:guid}/run" => ($"/api/jobs/definitions/{m.Job}/run", "{}"),
            "GET /api/jobs/runs/{id:guid}" => ($"/api/jobs/runs/{m.Finished}", null),
            "POST /api/jobs/runs/{id:guid}/rerun" => ($"/api/jobs/runs/{m.Finished}/rerun", "{}"),
            "POST /api/jobs/runs/{id:guid}/cancel" => ($"/api/jobs/runs/{m.Queued}/cancel", "{}"),
            _ => throw new InvalidOperationException($"No probe target for job endpoint {key}; extend JobProbe."),
        };
    }

    /// <summary>Fresh material in one customer, written as the database owner would.</summary>
    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        await using var c = await fx.OpenAdminAsync(code, ct);
        var admin = Guid.CreateVersion7();
        var name = "jbp" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, ct, admin, name, system);
        await ExecAsync(c, """
            insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
            select $1, $2, r.id, null, 'probe', current_date - 1, now(), $3, now(), $3 from iam.roles r where r.code = 'tenant_admin'
            """, ct, Guid.CreateVersion7(), admin, system);
        var job = await ScalarAsync<Guid>(c, "select id from job.definitions where code = 'kernel.purge'", ct);
        var version = await ScalarAsync<long>(c, "select row_version from job.definitions where code = 'kernel.purge'", ct);
        var (finished, queued) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecAsync(c, """
            insert into job.runs (id, definition_id, status, priority, attempt, parameters, started_at, finished_at, created_at, created_by, updated_at, updated_by)
            values ($1, $3, 'succeeded', 0, 1, '{}', now(), now(), now(), $4, now(), $4),
                   ($2, $3, 'queued', 0, 0, '{}', null, null, now(), $4, now(), $4)
            """, ct, finished, queued, job, system);
        return new Material(admin, job, version, finished, queued);
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
