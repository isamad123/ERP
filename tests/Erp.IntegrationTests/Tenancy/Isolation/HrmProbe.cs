using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the purpose-built HR endpoints (slice S1.20a onward, <c>HrmEndpointMetadata</c>). Each customer
/// gets fresh material made through its own API: a person with an identifier, contact, address, relation and qualification, a
/// second person, and a third with the first one's name and birth date sent to the duplicate review. Then, as for the other probes:
/// <list type="number">
/// <item>B's host and token aimed at A's ids, with and without headers naming A: actions on A's records are 404; creates and lists
/// answer B's own data only; never 5xx; nothing of A's in any answer.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class HrmProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(
        Guid Person, long PersonVersion, Guid Into, Guid Identifier, long IdentifierVersion, Guid Contact, long ContactVersion, Guid Address, long AddressVersion,
        Guid Relation, long RelationVersion, Guid Qualification, long QualificationVersion, Guid Case, long CaseVersion, string PersonNo);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);
        var a = await MaterialAsync(fx.HostA, tokenA, ct);
        await MaterialAsync(fx.HostB, tokenB, ct); // B's own records, so B's calls have real rows of their own to hit
        var markers = new[] { a.Person, a.Into, a.Identifier, a.Contact, a.Address, a.Relation, a.Qualification, a.Case }.Select(g => g.ToString()).ToList(); // person numbers repeat across customers, so ids only
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
            else if (endpoint.Kind == EndpointKind.HrmRecordAction && reply.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: expected 404 for A's record, got {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.HrmCatalog && reply.Status != HttpStatusCode.OK)
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
        else if (endpoint.Pattern == "/api/hrm/duplicates" && !own.Body.Contains(a.Case.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{endpoint}: positive control failed: A's review queue does not list A's case {a.Case}.");
        }

        return failures;
    }

    private static (string Path, string? Body) Target(ApiEndpoint endpoint, Material m)
    {
        var p = $"/api/hrm/persons/{m.Person}";
        return (endpoint.Method + " " + endpoint.Pattern) switch
        {
            "POST /api/hrm/persons" => ("/api/hrm/persons", Json(new { firstName = "Probe" + Guid.NewGuid().ToString("N")[..8], gender = "unspecified" })),
            "PUT /api/hrm/persons/{id:guid}" => (p, Json(new { lastName = "Changed", rowVersion = m.PersonVersion })),
            "POST /api/hrm/persons/{id:guid}/merge" => ($"{p}/merge", Json(new { intoPersonId = m.Into, reason = "probe" })),
            "POST /api/hrm/persons/{id:guid}/identifiers" => ($"{p}/identifiers", Json(new { kind = "passport", countryCode = "PK", value = "PX" + Random.Shared.Next(1_000_000, 9_999_999) })),
            "PUT /api/hrm/persons/{id:guid}/identifiers/{rowId:guid}" => ($"{p}/identifiers/{m.Identifier}", Json(new { issueDate = "2020-01-01", rowVersion = m.IdentifierVersion })),
            "DELETE /api/hrm/persons/{id:guid}/identifiers/{rowId:guid}" => ($"{p}/identifiers/{m.Identifier}?rowVersion={m.IdentifierVersion}", null),
            "GET /api/hrm/persons/{id:guid}/identifiers/{rowId:guid}/value" => ($"{p}/identifiers/{m.Identifier}/value", null),
            "POST /api/hrm/persons/{id:guid}/contacts" => ($"{p}/contacts", Json(new { kind = "phone", value = "021-1234567" })),
            "PUT /api/hrm/persons/{id:guid}/contacts/{rowId:guid}" => ($"{p}/contacts/{m.Contact}", Json(new { value = "0300-0000001", rowVersion = m.ContactVersion })),
            "DELETE /api/hrm/persons/{id:guid}/contacts/{rowId:guid}" => ($"{p}/contacts/{m.Contact}?rowVersion={m.ContactVersion}", null),
            "POST /api/hrm/persons/{id:guid}/addresses" => ($"{p}/addresses", Json(new { kind = "mailing", address = new { line1 = "PO Box 1" }, validFrom = "2024-01-01" })),
            "PUT /api/hrm/persons/{id:guid}/addresses/{rowId:guid}" => ($"{p}/addresses/{m.Address}", Json(new { address = new { line1 = "Changed" }, rowVersion = m.AddressVersion })),
            "DELETE /api/hrm/persons/{id:guid}/addresses/{rowId:guid}" => ($"{p}/addresses/{m.Address}?rowVersion={m.AddressVersion}", null),
            "POST /api/hrm/persons/{id:guid}/relations" => ($"{p}/relations", Json(new { relation = "child", fullName = "Probe Child", isDependent = true })),
            "PUT /api/hrm/persons/{id:guid}/relations/{rowId:guid}" => ($"{p}/relations/{m.Relation}", Json(new { fullName = "Changed", rowVersion = m.RelationVersion })),
            "DELETE /api/hrm/persons/{id:guid}/relations/{rowId:guid}" => ($"{p}/relations/{m.Relation}?rowVersion={m.RelationVersion}", null),
            "POST /api/hrm/persons/{id:guid}/qualifications" => ($"{p}/qualifications", Json(new { kind = "language", title = "Urdu" })),
            "PUT /api/hrm/persons/{id:guid}/qualifications/{rowId:guid}" => ($"{p}/qualifications/{m.Qualification}", Json(new { title = "Changed", rowVersion = m.QualificationVersion })),
            "DELETE /api/hrm/persons/{id:guid}/qualifications/{rowId:guid}" => ($"{p}/qualifications/{m.Qualification}?rowVersion={m.QualificationVersion}", null),
            "GET /api/hrm/duplicates" => ("/api/hrm/duplicates", null),
            "POST /api/hrm/duplicates/{id:guid}/distinct" => ($"/api/hrm/duplicates/{m.Case}/distinct", Json(new { note = "probe", rowVersion = m.CaseVersion })),
            "GET /api/hrm/person-catalogue" => ("/api/hrm/person-catalogue", null),
            var key => throw new InvalidOperationException($"No probe target for HR endpoint {key}; extend HrmProbe."),
        };
    }

    /// <summary>Fresh records in one customer, made through its own API (allow-all access in the isolation host).</summary>
    private async Task<Material> MaterialAsync(string host, string token, CancellationToken ct)
    {
        var first = "Iso" + Guid.NewGuid().ToString("N")[..10];
        var birth = new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(0, 9000)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var cnic = string.Concat(Enumerable.Range(0, 13).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var created = await PostAsync(host, token, "/api/hrm/persons", new
        {
            firstName = first,
            lastName = "Probe",
            gender = "female",
            dateOfBirth = birth,
            identifiers = new[] { new { kind = "cnic", countryCode = "PK", value = cnic } },
            contacts = new[] { new { kind = "mobile", value = "0300-1234567", isPrimary = true } },
            addresses = new[] { new { kind = "permanent", address = new { line1 = "House 1" }, validFrom = "2020-01-01" } },
            relations = new[] { new { relation = "spouse", fullName = "Probe Spouse", isEmergencyContact = true } },
            qualifications = new[] { new { kind = "education", title = "BSc" } },
        }, ct);
        var person = created.GetProperty("id").GetGuid();
        var into = (await PostAsync(host, token, "/api/hrm/persons", new { firstName = "Into" + first, gender = "male" }, ct)).GetProperty("id").GetGuid();
        await PostAsync(host, token, "/api/hrm/persons", new { firstName = first, lastName = "Probe", gender = "female", dateOfBirth = birth, duplicates = "review" }, ct);
        var cases = await GetAsync(host, token, "/api/hrm/duplicates?limit=200", ct);
        var match = cases.GetProperty("items").EnumerateArray().Last(c => c.GetProperty("other").GetProperty("id").GetGuid() == person);
        async Task<(Guid Id, long Version)> FirstOf(string list)
        {
            var row = (await GetAsync(host, token, $"{list}?personId={person}", ct)).GetProperty("items")[0];
            return (row.GetProperty("id").GetGuid(), row.GetProperty("rowVersion").GetInt64());
        }

        var identifier = await FirstOf("/api/hrm/person-identifiers");
        var contact = await FirstOf("/api/hrm/person-contacts");
        var address = await FirstOf("/api/hrm/person-addresses");
        var relation = await FirstOf("/api/hrm/person-relations");
        var qualification = await FirstOf("/api/hrm/person-qualifications");
        return new Material(person, created.GetProperty("rowVersion").GetInt64(), into, identifier.Id, identifier.Version, contact.Id, contact.Version,
            address.Id, address.Version, relation.Id, relation.Version, qualification.Id, qualification.Version,
            match.GetProperty("id").GetGuid(), match.GetProperty("rowVersion").GetInt64(), created.GetProperty("personNo").GetString()!);
    }

    private async Task<JsonElement> PostAsync(string host, string token, string path, object body, CancellationToken ct)
    {
        var reply = await SendAsync(host, token, HttpMethod.Post, path, Json(body), hints: false, ct);
        return reply.Status == HttpStatusCode.Created ? JsonDocument.Parse(reply.Body).RootElement.Clone()
            : throw new InvalidOperationException($"HR probe material {path}: {(int)reply.Status} {Short(reply.Body)}");
    }

    private async Task<JsonElement> GetAsync(string host, string token, string path, CancellationToken ct)
    {
        var reply = await SendAsync(host, token, HttpMethod.Get, path, null, hints: false, ct);
        return reply.Status == HttpStatusCode.OK ? JsonDocument.Parse(reply.Body).RootElement.Clone()
            : throw new InvalidOperationException($"HR probe material {path}: {(int)reply.Status} {Short(reply.Body)}");
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

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);
}
