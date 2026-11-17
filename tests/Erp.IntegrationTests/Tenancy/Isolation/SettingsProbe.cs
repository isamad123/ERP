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
/// The cross-customer probe of the settings and feature-flag endpoints (slice S1.09a). Each run registers, in both customers,
/// the same test setting (self-service, tenant, company and user layers) and test flag, and gives each customer a company of
/// its own with a company value, its history row and a flag override. Then, as for the other purpose-built endpoints:
/// <list type="number">
/// <item>B's host and token aimed at A's ids (query, body, route), with and without headers naming A: reads answer B's own
/// data (200) with nothing of A's; changes naming A's company or history row are refused; the caller's own change acts on B
/// only; never 5xx.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class SettingsProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(string Key, string Flag, Guid CompanyId, Guid HistoryId);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (key, flag) = ("test.iso_" + suffix, "test.iso_flag_" + suffix);
        var a = await MaterialAsync(fx.CodeA, key, flag, 4242, ct);
        await MaterialAsync(fx.CodeB, key, flag, 1313, ct);
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);
        var markers = new[] { a.CompanyId.ToString(), a.HistoryId.ToString() };
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
            else if (endpoint.Kind == EndpointKind.SettingsRead && reply.Status != HttpStatusCode.OK)
            {
                failures.Add($"{endpoint} [{label}]: B's own read was refused ({(int)reply.Status}); the path was not exercised. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.SettingsWrite && (int)reply.Status is >= 200 and < 300)
            {
                failures.Add($"{endpoint} [{label}]: a change naming A's records was accepted on B's host ({(int)reply.Status}). {Short(reply.Body)}");
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

        var own = await SendAsync(fx.HostA, tokenA, method, path, body, hints: false, ct);
        if ((int)own.Status is < 200 or >= 300)
        {
            failures.Add($"{endpoint}: positive control failed: A was not served on its own host ({(int)own.Status}). {Short(own.Body)}");
        }

        return failures;
    }

    private static (string Path, string? Body) Target(ApiEndpoint endpoint, Material m)
    {
        var path = endpoint.Pattern.Replace("{key}", endpoint.Pattern.Contains("feature-flags", StringComparison.Ordinal) ? m.Flag : m.Key, StringComparison.Ordinal)
            .Replace("{id:guid}", m.HistoryId.ToString(), StringComparison.Ordinal);
        var company = new { layer = "company", scopeId = m.CompanyId, reason = "probe" };
        return (endpoint.Method + " " + endpoint.Pattern) switch
        {
            "GET /api/cfg/settings" => ($"{path}?companyId={m.CompanyId}&search={m.Key}", null),
            "GET /api/cfg/settings/{key}" => ($"{path}?companyId={m.CompanyId}", null),
            "PUT /api/cfg/settings/{key}" => (path, Json(new { layer = "company", scopeId = m.CompanyId, value = 7, reason = "probe" })),
            "POST /api/cfg/settings/{key}/reset" => (path, Json(company)),
            "GET /api/cfg/settings/{key}/history" => ($"{path}?layer=company&scopeId={m.CompanyId}", null),
            "POST /api/cfg/settings/history/{id:guid}/revert" => (path, Json(new { reason = "probe" })),
            "GET /api/cfg/me/settings/{key}" => (path, null),
            "PUT /api/cfg/me/settings/{key}" => (path, Json(new { value = 7 })),
            "GET /api/cfg/feature-flags" => ($"{path}?companyId={m.CompanyId}", null),
            "PUT /api/cfg/feature-flags/{key}" => (path, Json(new { layer = "company", scopeId = m.CompanyId, enabled = true, reason = "probe" })),
            "POST /api/cfg/feature-flags/{key}/reset" => (path, Json(company)),
            var key => throw new InvalidOperationException($"No probe target for settings endpoint {key}; extend SettingsProbe."),
        };
    }

    /// <summary>The test setting and flag, a company of the customer's own with a company value, its history row and a flag override.</summary>
    private async Task<Material> MaterialAsync(string code, string key, string flag, int value, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var (company, row, history) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, """
            insert into org.companies (id, code, name_i18n, legal_name, country_code, base_currency_code, default_language, cost_center_model, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Probe company"}', 'Probe company', 'PK', 'PKR', 'en', 'none', 'active', now(), $3, now(), $3)
            """, ct, company, "SP" + Guid.NewGuid().ToString("N")[..8], system);
        await ExecAsync(c, """
            insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n, is_visible, area, self_service)
            values ($1, 'test', 'int', '1', '{}', '{tenant,company,user}', false, '{"en":"Probe","ur":"جانچ"}', true, 'test', true)
            """, ct, key);
        await ExecAsync(c, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'company', $3, to_jsonb($4), now(), $5, now(), $5)
            """, ct, row, key, company, value, system);
        await ExecAsync(c, """
            insert into cfg.setting_value_history (id, setting_value_id, setting_key, layer, scope_id, old_value, new_value, reason, created_at, created_by)
            values ($1, $2, $3, 'company', $4, null, to_jsonb($5), 'probe', now(), $6)
            """, ct, history, row, key, company, value, system);
        await ExecAsync(c, """
            insert into cfg.feature_flags (key, default_enabled, description_i18n, module_id) values ($1, false, '{"en":"Probe","ur":"جانچ"}', 'test')
            """, ct, flag);
        await ExecAsync(c, """
            insert into cfg.feature_flag_overrides (id, flag_key, layer, scope_id, enabled, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'company', $3, true, now(), $4, now(), $4)
            """, ct, Guid.CreateVersion7(), flag, company, system);
        return new Material(key, flag, company, history);
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

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);
}
