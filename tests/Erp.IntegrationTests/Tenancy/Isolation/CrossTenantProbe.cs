using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.CodeGen.Emit;
using Microsoft.AspNetCore.Builder;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// Tries to reach customer A's data through one endpoint in every crossed way (S1.04 build item 2) and returns what
/// went wrong; an empty list means the endpoint kept the customers apart. The probe never asserts itself, so the leak
/// self-test can run it against a deliberately broken endpoint and check that it reports the leak.
/// <list type="number">
/// <item>B's host and B's token, with A's ids in the path, the list filters and the body: 404 on reads by id, updates and
/// deletes; lists and creates never return A's data; nothing answers 5xx.</item>
/// <item>The same, with A named in headers and the query string (X-Tenant, X-Forwarded-Host, Forwarded, ?tenant=): same results.</item>
/// <item>A's host with B's token, B's host with A's token, A's host with no token or a forged token: 401 (product owner,
/// 2026-11-15: a token that is not valid for the host is 401).</item>
/// <item>An unknown host with A's token: 404.</item>
/// <item>A's database is byte-for-byte unchanged by all of the above.</item>
/// <item>Positive controls, so a pass is never vacuous: A reads, lists, updates and deletes its own row; B lists its own row.</item>
/// </list>
/// </summary>
public sealed class CrossTenantProbe(IsolationFixture fx, WebApplication app)
{
    private const int MaxPages = 50;

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Kind is EndpointKind.IdentityAnonymous or EndpointKind.IdentitySelf or EndpointKind.IdentityUserAction)
        {
            return await new IdentityProbe(fx, app).RunAsync(endpoint, ct);
        }

        if (endpoint.Kind is EndpointKind.AccessCreate or EndpointKind.AccessRecordAction or EndpointKind.AccessQuery or EndpointKind.AccessCatalog)
        {
            return await new AccessProbe(fx, app).RunAsync(endpoint, ct);
        }

        if (endpoint.Kind is EndpointKind.NumberingCreate or EndpointKind.NumberingRecordAction or EndpointKind.NumberingCatalog or EndpointKind.NumberingQuery)
        {
            return await new NumberingProbe(fx, app).RunAsync(endpoint, ct);
        }

        if (endpoint.Kind is EndpointKind.SettingsRead or EndpointKind.SettingsWrite or EndpointKind.SettingsSelf)
        {
            return await new SettingsProbe(fx, app).RunAsync(endpoint, ct);
        }

        if (endpoint.Kind is EndpointKind.WorkflowCatalog or EndpointKind.WorkflowRecordAction or EndpointKind.WorkflowCreate or EndpointKind.WorkflowBulk)
        {
            return await new WorkflowProbe(fx, app).RunAsync(endpoint, ct);
        }

        if (endpoint.Kind == EndpointKind.Licence)
        {
            return await new LicenceProbe(fx, app).RunAsync(endpoint, ct);
        }

        if (endpoint.Kind is EndpointKind.AuditQuery or EndpointKind.AuditRecord or EndpointKind.AuditSelf or EndpointKind.AuditVerify)
        {
            return await new AuditProbe(fx, app).RunAsync(endpoint, ct);
        }

        var failures = new List<string>();
        if (endpoint.Kind == EndpointKind.Unclassified || endpoint.Resource is null)
        {
            failures.Add($"{endpoint}: the suite cannot probe this endpoint (no GeneratedEndpointMetadata naming its table, or a route shape other than list/get/create/update/delete). Extend CrossTenantProbe before mapping it.");
            return failures;
        }

        var table = IsolationFixture.Table(endpoint.Resource);
        var rowA = await fx.RowAsync(fx.CodeA, table, ct);
        var rowB = await fx.RowAsync(fx.CodeB, table, ct);
        var idA = (Guid)rowA["id"]!;
        var idB = (Guid)rowB["id"]!;
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);

        // A's ids (the row and every row it references) must never reach B. Shared baseline ids are not markers.
        var valuesB = rowB.Values.OfType<Guid>().ToHashSet();
        var markers = rowA.Values.OfType<Guid>().Where(g => !valuesB.Contains(g)).Select(g => g.ToString()).ToHashSet(StringComparer.Ordinal);

        // Positive control 1, and the request body: A reads its own row through A's host.
        var own = await SendAsync(fx.HostA, tokenA, HttpMethod.Get, $"{endpoint.BasePath}/{idA}", null, hints: false, ct);
        if (own.Status != HttpStatusCode.OK)
        {
            failures.Add($"{endpoint}: positive control failed: A could not read its own row {idA} (got {(int)own.Status}); the probe has no valid data. {Short(own.Body)}");
            return failures;
        }

        var body = own.Body;
        using var template = JsonDocument.Parse(body);
        var rowVersion = template.RootElement.TryGetProperty("rowVersion", out var rv) ? rv.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        var path = endpoint.Kind switch
        {
            EndpointKind.Get or EndpointKind.Update => $"{endpoint.BasePath}/{idA}",
            EndpointKind.Delete => $"{endpoint.BasePath}/{idA}?rowVersion={rowVersion}",
            EndpointKind.List => endpoint.BasePath + "?" + FilterQuery(table, rowA) + "&limit=200",
            _ => endpoint.BasePath,
        };
        var method = new HttpMethod(endpoint.Method);
        var sendsBody = endpoint.Kind is EndpointKind.Create or EndpointKind.Update;

        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        // 1 and 2: B's own host and token, aimed at A's data, with and without hints naming A.
        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var response = await SendAsync(fx.HostB, tokenB, method, path, sendsBody ? body : null, hints, ct);
            if ((int)response.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)response.Status}. {Short(response.Body)}");
            }

            switch (endpoint.Kind)
            {
                case EndpointKind.Get or EndpointKind.Update or EndpointKind.Delete:
                    Expect(failures, endpoint, label, response, HttpStatusCode.NotFound);
                    break;
                case EndpointKind.List:
                    Expect(failures, endpoint, label, response, HttpStatusCode.OK);
                    var pages = await PageAsync(fx.HostB, tokenB, endpoint.BasePath, hints, ct);
                    await LeaksAsync(failures, endpoint, label + ", unfiltered list", fx.CodeB, string.Concat(pages.Bodies), markers, null, ct);
                    break;
                case EndpointKind.Create when response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    failures.Add($"{endpoint} [{label}]: B was refused on its own host ({(int)response.Status}); the create path was not exercised.");
                    break;
            }

            // An echo of what the caller itself sent is not a leak; anything else of A's is.
            await LeaksAsync(failures, endpoint, label, fx.CodeB, response.Body, markers, sendsBody ? body + path : null, ct);
        }

        // 3 and 4: crossed host and token, wrong or missing tokens, unknown host.
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var crossed = new (string Label, string Host, string? Token, HttpStatusCode Expected)[]
        {
            ("A host + B token", fx.HostA, tokenB, HttpStatusCode.Unauthorized),
            ("B host + A token", fx.HostB, tokenA, HttpStatusCode.Unauthorized),
            ("A host + no token", fx.HostA, null, HttpStatusCode.Unauthorized),
            ("A host + token signed by an untrusted key", fx.HostA, fx.Tenancy.Token(fx.CodeA, key: forger), HttpStatusCode.Unauthorized),
            ("unknown host + A token", "unknown-" + fx.Tenancy.Suffix + ".erp.test", tokenA, HttpStatusCode.NotFound),
        };
        foreach (var (label, host, token, expected) in crossed)
        {
            var response = await SendAsync(host, token, method, path, sendsBody ? body : null, hints: false, ct);
            Expect(failures, endpoint, label, response, expected);
            await LeaksAsync(failures, endpoint, label, null, response.Body, markers, null, ct);
        }

        // 5: nothing of A's changed.
        var changed = IsolationFixture.Changed(before, await fx.FingerprintAsync(fx.CodeA, ct));
        if (changed.Count > 0)
        {
            failures.Add($"{endpoint}: customer A's database changed during the cross-customer calls: {string.Join(", ", changed)}");
        }

        // 6: positive controls (after the fingerprint, because they change A).
        switch (endpoint.Kind)
        {
            case EndpointKind.List:
                var mine = await PageAsync(fx.HostA, tokenA, endpoint.BasePath + "?" + FilterQuery(table, rowA), false, ct, idA);
                if (!mine.Found)
                {
                    failures.Add($"{endpoint}: positive control failed: A's list does not contain A's row {idA}.");
                }

                var theirs = await PageAsync(fx.HostB, tokenB, endpoint.BasePath, false, ct, idB);
                if (!theirs.Found)
                {
                    failures.Add($"{endpoint}: positive control failed: B's list does not contain B's own row {idB}.");
                }

                break;
            case EndpointKind.Update:
                var updated = await SendAsync(fx.HostA, tokenA, HttpMethod.Put, path, body, hints: false, ct);
                Expect(failures, endpoint, "positive control: A updates its own row", updated, HttpStatusCode.OK);
                break;
            case EndpointKind.Delete:
                var deleted = await SendAsync(fx.HostA, tokenA, HttpMethod.Delete, path, null, hints: false, ct);
                Expect(failures, endpoint, "positive control: A deletes its own row", deleted, HttpStatusCode.NoContent);
                fx.Forget(fx.CodeA, table);
                break;
        }

        return failures;
    }

    /// <summary>A query string that filters by every reference A's row holds (unknown names are ignored by the endpoint).</summary>
    private static string FilterQuery(ExpectedTable table, IReadOnlyDictionary<string, object?> row) =>
        string.Join("&", table.Columns
            .Where(c => c.Name != "id" && row.TryGetValue(c.Name, out var v) && v is Guid)
            .Select(c => Camel(c.Name) + "=" + ((Guid)row[c.Name]!).ToString()));

    private static string Camel(string snake)
    {
        var parts = snake.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static void Expect(List<string> failures, ApiEndpoint endpoint, string label, Reply response, HttpStatusCode expected)
    {
        if (response.Status != expected)
        {
            failures.Add($"{endpoint} [{label}]: expected {(int)expected}, got {(int)response.Status}. {Short(response.Body)}");
        }
    }

    /// <summary>
    /// A's ids in a response are a leak unless the caller sent them itself (an echo) or the caller's own database
    /// stores them: the probe's own cross-customer creates can store a copied reference in B (a plain id without a
    /// foreign key), and B listing that row later shows B's data, not A's. A's seeded row ids are never written to B,
    /// because no write request accepts an id, so reading A's rows through any path is still reported.
    /// </summary>
    private async Task LeaksAsync(List<string> failures, ApiEndpoint endpoint, string label, string? caller, string body, IReadOnlySet<string> markers, string? sent, CancellationToken ct)
    {
        var suspects = markers.Where(m => body.Contains(m, StringComparison.OrdinalIgnoreCase) && (sent is null || !sent.Contains(m, StringComparison.OrdinalIgnoreCase))).ToList();
        if (suspects.Count > 0 && caller is not null)
        {
            var own = await fx.StoredInAsync(caller, [.. suspects.Select(Guid.Parse)], ct);
            suspects = [.. suspects.Where(m => !own.Contains(Guid.Parse(m)))];
        }

        if (suspects.Count > 0)
        {
            failures.Add($"{endpoint} [{label}]: LEAK: the response contains customer A's id(s) {string.Join(", ", suspects)}. {Short(body)}");
        }
    }

    private async Task<(bool Found, List<string> Bodies)> PageAsync(string host, string token, string pathAndQuery, bool hints, CancellationToken ct, Guid? lookFor = null)
    {
        var bodies = new List<string>();
        Guid? after = null;
        for (var i = 0; i < MaxPages; i++)
        {
            var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var page = await SendAsync(host, token, HttpMethod.Get, pathAndQuery + separator + "limit=200" + (after is null ? string.Empty : "&after=" + after), null, hints, ct);
            bodies.Add(page.Body);
            if (page.Status != HttpStatusCode.OK)
            {
                break;
            }

            if (lookFor is not null && page.Body.Contains(lookFor.Value.ToString(), StringComparison.Ordinal))
            {
                return (true, bodies);
            }

            using var document = JsonDocument.Parse(page.Body);
            var next = document.RootElement.GetProperty("next");
            if (next.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            after = next.GetGuid();
        }

        return (false, bodies);
    }

    private async Task<Reply> SendAsync(string host, string? token, HttpMethod method, string pathAndQuery, string? body, bool hints, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(app, host, token);
        var target = pathAndQuery;
        if (hints)
        {
            var separator = target.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            target += separator + "tenant=" + fx.CodeA + "&tenantCode=" + fx.CodeA + "&tenant_code=" + fx.CodeA;
        }

        using var request = new HttpRequestMessage(method, target);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (hints)
        {
            request.Headers.Add("X-Tenant", fx.CodeA);
            request.Headers.Add("X-Tenant-Id", fx.CodeA);
            request.Headers.Add("X-Tenant-Code", fx.CodeA);
            request.Headers.Add("X-Forwarded-Host", fx.HostA);
            request.Headers.Add("X-Original-Host", fx.HostA);
            request.Headers.Add("Forwarded", "host=" + fx.HostA);
        }

        using var response = await client.SendAsync(request, ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);
}
