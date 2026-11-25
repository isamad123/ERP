using System.Net;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the API client endpoints (slice S1.27c). Each customer gets an administrator and one API client with its own name and
/// client id. Then B's host and token aimed at A's client id answer 404 (B's own list and a new client in B 200 and 201); crossed hosts and tokens 401; an unknown host 404;
/// A's database stays unchanged; and A is served on its own host. A key of A offered on B's host is refused too (401, see ApiClientTests).
/// </summary>
public sealed class ApiClientProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid Admin, Guid Client, string ClientId, string Name);

    private sealed record Plan(string Path, Func<HttpContent?> Content, HttpStatusCode ForB, HttpStatusCode ForOwn);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var markers = new[] { a.Client.ToString(), a.ClientId, a.Name };
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

        foreach (var (label, host, token, status) in new (string, string, string?, HttpStatusCode)[]
        {
            ("A host + B token", fx.HostA, tokenB, HttpStatusCode.Unauthorized),
            ("B host + A token", fx.HostB, tokenA, HttpStatusCode.Unauthorized),
            ("A host + no token", fx.HostA, null, HttpStatusCode.Unauthorized),
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
        const string Base = "/api/iam/api-clients";
        object Body() => new { name = "probe " + Guid.NewGuid().ToString("N")[..8], scopes = new[] { "hrm.attendance_devices.push" }, ipAllow = new[] { "10.0.0.0/8" }, rateLimitPerMinute = 10, expiresAt = (DateTimeOffset?)null };
        return key switch
        {
            "GET /api/iam/api-clients" => new Plan(Base, () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "GET /api/iam/api-clients/scope-options" => new Plan(Base + "/scope-options", () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "POST /api/iam/api-clients" => new Plan(Base, () => Json(Body()), HttpStatusCode.Created, HttpStatusCode.Created),
            "GET /api/iam/api-clients/{id:guid}" => new Plan($"{Base}/{m.Client}", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/iam/api-clients/{id:guid}/rotate" => new Plan($"{Base}/{m.Client}/rotate", () => Json(new { }), HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/iam/api-clients/{id:guid}/revoke" => new Plan($"{Base}/{m.Client}/revoke", () => Json(new { }), HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/iam/api-clients/{id:guid}/usage" => new Plan($"{Base}/{m.Client}/usage", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            _ => throw new InvalidOperationException($"No probe target for API client endpoint {key}; extend ApiClientProbe."),
        };
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var admin = Guid.CreateVersion7();
        var client = Guid.CreateVersion7();
        var name = "adm" + Guid.NewGuid().ToString("N")[..12];
        var clientId = "ak_probe" + Guid.NewGuid().ToString("N")[..16];
        var clientName = "Probe client " + Guid.NewGuid().ToString("N")[..10]; // unique to this customer's material, so it can be told from B's own
        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, ct, admin, name, system);
        await ExecAsync(c, """
            insert into iam.api_clients (id, name, client_id, secret_hash, scopes, ip_allow, rate_limit_per_minute, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, '$argon2id$probe', array['hrm.attendance_devices.push'], array['10.0.0.0/8'], 10, 'active', now(), $4, now(), $4)
            """, ct, client, clientName, clientId, admin);
        return new Material(admin, client, clientId, clientName);
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

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
