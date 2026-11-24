using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the export centre endpoints (slice S1.16b). Each customer gets a person, its company and one export whose
/// file has passed its keeping date. Then B's host and token aimed at A's ids answer 404 (a request naming A's company 400, B's own list 200);
/// crossed hosts and tokens 401; unknown host 404; A's database stays unchanged; and A is served on its own host (its own expired export answers 410).
/// </summary>
public sealed class ExportProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid Admin, Guid Company, Guid Export, string FileName);

    private sealed record Plan(string Path, Func<HttpContent?> Content, HttpStatusCode ForB, HttpStatusCode ForOwn);

        public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var markers = new[] { a.Admin, a.Company, a.Export }.Select(g => g.ToString()).Append(a.FileName).ToArray();
        var method = new HttpMethod(endpoint.Method);
        var plan = Target(endpoint, a);
        var tokenA = fx.Tenancy.Token(fx.CodeA, user: a.Admin);
        var tokenB = fx.Tenancy.Token(fx.CodeB, user: b.Admin);
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var reply = await SendAsync(fx.HostB, tokenB, method, plan.Path, plan.Content(), hints, ct);
            if ((int)reply.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (reply.Status != plan.ForB)
            {
                failures.Add($"{endpoint} [{label}]: expected {(int)plan.ForB}, got {(int)reply.Status}. {Short(reply.Body)}");
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
            var reply = await SendAsync(host, token, method, plan.Path, plan.Content(), hints: false, ct);
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

        var own = await SendAsync(fx.HostA, tokenA, method, plan.Path, plan.Content(), hints: false, ct);
        if (own.Status != plan.ForOwn)
        {
            failures.Add($"{endpoint}: positive control failed: A on its own host expected {(int)plan.ForOwn}, got {(int)own.Status}. {Short(own.Body)}");
        }

        return failures;
    }

    private static Plan Target(ApiEndpoint endpoint, Material m)
    {
        var key = endpoint.Method + " " + endpoint.Pattern.TrimEnd('/');
        const string Base = "/api/itg/exports";
        object Body(string reason) => new { source = "org.branches", query = new { }, format = "csv", reason, companyId = m.Company, language = "en", columns = new[] { new { field = "code", label = "Code" } } };
        return key switch
        {
            "POST /api/itg/exports/check" => new Plan(Base + "/check", () => Json(Body("probe")), HttpStatusCode.BadRequest, HttpStatusCode.OK),
            "POST /api/itg/exports" => new Plan(Base, () => Json(Body("probe")), HttpStatusCode.BadRequest, HttpStatusCode.Accepted),
            "GET /api/itg/exports" => new Plan(Base, () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "GET /api/itg/exports/{id:guid}" => new Plan($"{Base}/{m.Export}", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/itg/exports/{id:guid}/download" => new Plan($"{Base}/{m.Export}/download", () => null, HttpStatusCode.NotFound, HttpStatusCode.Gone),
            _ => throw new InvalidOperationException($"No probe target for export endpoint {key}; extend ExportProbe."),
        };
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var admin = Guid.CreateVersion7();
        var export = Guid.CreateVersion7();
        var name = "exp" + Guid.NewGuid().ToString("N")[..12];
        var fileName = $"probe-{export:N}.csv"; // unique to this customer's material, so it can be told from B's own
        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, ct, admin, name, system);
        var company = await ScalarAsync<Guid>(c, "select id from org.companies order by created_at limit 1", ct);
        await ExecAsync(c, """
            insert into itg.export_jobs (id, company_id, entity_type, filter, format, status, row_count, file_name, expires_at, finished_at, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'org.branches', '{"query":{},"columns":[]}'::jsonb, 'csv', 'done', 1, $3, now() - interval '1 day', now() - interval '8 days', now() - interval '8 days', $4, now() - interval '8 days', $4)
            """, ct, export, company, fileName, admin);
        return new Material(admin, company, export, fileName);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string host, string? token, HttpMethod method, string pathAndQuery, HttpContent? content, bool hints, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(app, host, token);
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var target = hints ? pathAndQuery + separator + "tenant=" + fx.CodeA + "&tenantCode=" + fx.CodeA : pathAndQuery;
        using var request = new HttpRequestMessage(method, target) { Content = content };
        if (hints)
        {
            request.Headers.Add("X-Tenant", fx.CodeA);
            request.Headers.Add("X-Tenant-Code", fx.CodeA);
            request.Headers.Add("X-Forwarded-Host", fx.HostA);
            request.Headers.Add("Forwarded", "host=" + fx.HostA);
        }

        using var response = await client.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
