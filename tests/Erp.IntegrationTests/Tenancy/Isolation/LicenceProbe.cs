using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Erp.Kernel.Domain.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the licence endpoints (slice S1.09b): capabilities, the licence page and the usage
/// statements. Customer A's licence gets a limit value and a usage reading found nowhere else; then:
/// <list type="number">
/// <item>B's host and token, with and without headers and query naming A: 200 with B's licence only; never A's values.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; a positive control on A's own host answers A's own values.</item>
/// </list>
/// </summary>
public sealed class LicenceProbe(IsolationFixture fx, WebApplication app)
{
    private const long Marker = 424242;
    private const long UsageMarker = 4242421;

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        await MaterialAsync(ct);
        var failures = new List<string>();
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);
        var markers = new[] { Marker.ToString(CultureInfo.InvariantCulture), UsageMarker.ToString(CultureInfo.InvariantCulture), fx.CodeA };
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var reply = await SendAsync(fx.HostB, tokenB, endpoint.Pattern, hints, ct);
            if (reply.Status != HttpStatusCode.OK)
            {
                failures.Add($"{endpoint} [{label}]: B's own read was not served ({(int)reply.Status}). {Short(reply.Body)}");
            }

            var leaked = markers.Where(m => reply.Body.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
            if (leaked.Count > 0)
            {
                failures.Add($"{endpoint} [{label}]: LEAK: the response contains customer A's values {string.Join(", ", leaked)}. {Short(reply.Body)}");
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
            var reply = await SendAsync(host, token, endpoint.Pattern, hints: false, ct);
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

        var own = await SendAsync(fx.HostA, tokenA, endpoint.Pattern, hints: false, ct);
        if (own.Status != HttpStatusCode.OK)
        {
            failures.Add($"{endpoint}: positive control failed: A was not served on its own host ({(int)own.Status}). {Short(own.Body)}");
        }
        else if (endpoint.Pattern != "/api/kernel/capabilities" && !markers.Take(2).Any(m => own.Body.Contains(m, StringComparison.Ordinal)))
        {
            failures.Add($"{endpoint}: positive control failed: A's own answer does not show A's licence values. {Short(own.Body)}");
        }

        return failures;
    }

    /// <summary>A's API-call limit and this month's API-call reading carry values that B's never have.</summary>
    private async Task MaterialAsync(CancellationToken ct)
    {
        await fx.Tenancy.SetClaimAsync(fx.CodeA, "limits,api.calls", Marker, ct);
        var period = LicenceStates.Today(DateTimeOffset.UtcNow, LicenceStates.Zone("Asia/Karachi")).ToString("yyyyMM", CultureInfo.InvariantCulture);
        await using var cp = await fx.Tenancy.OpenControlPlaneAsync(ct);
        await TenancyFixture.ExecAsync(cp, """
            insert into cp.usage_meters (id, tenant_id, meter_key, period, value, as_of, created_at, created_by, updated_at, updated_by)
            select $1, t.id, 'api_calls', $2, $3, now(), now(), $4, now(), $4 from cp.tenants t where t.code = $5
            on conflict (tenant_id, meter_key, period) do update set value = excluded.value
            """, ct, Guid.CreateVersion7(), period, UsageMarker, BaselineSeed.SystemUserId, fx.CodeA);
        TenancyFixture.ForgetCachedHosts(app); // the host look-up carries the entitlements; the claim was changed with SQL
    }

    private async Task<Reply> SendAsync(string host, string? token, string path, bool hints, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(app, host, token);
        using var request = new HttpRequestMessage(HttpMethod.Get, hints ? path + "?tenant=" + fx.CodeA + "&tenantCode=" + fx.CodeA : path);
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

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);

}
