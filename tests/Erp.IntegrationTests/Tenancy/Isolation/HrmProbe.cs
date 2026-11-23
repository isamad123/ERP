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
/// The cross-customer probe of the purpose-built HR endpoints (slice S1.20a onward, <c>HrmEndpointMetadata</c>). Each customer
/// gets fresh material made through its own API: a person with an identifier, contact, address, relation and qualification, a
/// second person, and a third with the first one's name and birth date sent to the duplicate review; from S1.20b two hired
/// employments, the first linked to a user; from S1.20c a second company, a former employment, a contract and a probation; from S1.20d a
/// payment account and a statutory registration of the first employment. Then, as for
/// the other probes:
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
        Guid Relation, long RelationVersion, Guid Qualification, long QualificationVersion, Guid Case, long CaseVersion, string PersonNo,
        Hire Org, Guid Employment, long EmploymentVersion, Guid Unlinked, long UnlinkedVersion, Guid FreeUser,
        Hire To, Guid Former, Guid Contract, long ContractVersion, long ProbationVersion, Guid BankAccount, Guid Registration, long RegistrationVersion,
        Guid Punching, Guid Location, string QrCode, string Badge);

    /// <summary>Where the probe hires (slice S1.20b): an org unit of a company and that company's branch.</summary>
    private sealed record Hire(Guid Company, Guid OrgUnit, Guid Branch);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);
        var a = await MaterialAsync(fx.HostA, tokenA, ct);
        await MaterialAsync(fx.HostB, tokenB, ct); // B's own records, so B's calls have real rows of their own to hit
        var markers = new[] { a.Person, a.Into, a.Identifier, a.Contact, a.Address, a.Relation, a.Qualification, a.Case, a.Employment, a.Unlinked, a.Former, a.Contract, a.To.Company, a.BankAccount, a.Registration, a.Punching, a.Location }.Select(g => g.ToString()).ToList(); // person numbers repeat across customers, so ids only
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

            // S1.20b employments: a hire into A's company (B has no such company), and actions on A's employments.
            "POST /api/hrm/employments" => ("/api/hrm/employments", Json(HireBody(m.Org, "Hire"))),
            "POST /api/hrm/employments/{id:guid}/events" => ($"/api/hrm/employments/{m.Employment}/events", Json(Acting(m.EmploymentVersion))),
            "POST /api/hrm/employments/{id:guid}/events/check" => ($"/api/hrm/employments/{m.Employment}/events/check", Json(Acting(m.EmploymentVersion))),
            "GET /api/hrm/employments/{id:guid}/as-of" => ($"/api/hrm/employments/{m.Employment}/as-of?date=2025-01-01", null),
            "GET /api/hrm/employments/{id:guid}/timeline" => ($"/api/hrm/employments/{m.Employment}/timeline", null),
            "PUT /api/hrm/employments/{id:guid}/user" => ($"/api/hrm/employments/{m.Unlinked}/user", Json(new { userId = m.FreeUser, rowVersion = m.UnlinkedVersion })),
            "DELETE /api/hrm/employments/{id:guid}/user" => ($"/api/hrm/employments/{m.Employment}/user?rowVersion={m.EmploymentVersion}", null),
            "GET /api/hrm/employment-catalogue" => ("/api/hrm/employment-catalogue", null),
            // S1.21a: B's own employee list is served and never shows A's employees (the search names A's probe person).
            "GET /api/hrm/employee-list" => ("/api/hrm/employee-list?limit=50", null),

            // S1.20c: a transfer of A's employment to A's second company, a rehire from A's former employment, contracts and probation.
            "POST /api/hrm/employments/{id:guid}/transfer" => ($"/api/hrm/employments/{m.Employment}/transfer", Json(Transfer(m))),
            "POST /api/hrm/employments/{id:guid}/transfer/check" => ($"/api/hrm/employments/{m.Employment}/transfer/check", Json(Transfer(m))),
            "POST /api/hrm/employments/{id:guid}/rehire" => ($"/api/hrm/employments/{m.Former}/rehire", Json(new
            {
                joiningDate = "2025-03-01", employeeType = "permanent", assignment = new { orgUnitId = m.Org.OrgUnit, branchId = m.Org.Branch, title = "Rehired probe" },
            })),
            "POST /api/hrm/employments/{id:guid}/contracts" => ($"/api/hrm/employments/{m.Employment}/contracts", Json(new { startDate = "2025-01-01", endDate = "2025-12-31", terms = new { hours = "40" } })),
            "POST /api/hrm/contracts/{id:guid}/renew" => ($"/api/hrm/contracts/{m.Contract}/renew", Json(new { endDate = "2026-12-31", rowVersion = m.ContractVersion })),
            "POST /api/hrm/employments/{id:guid}/probation/extend" => ($"/api/hrm/employments/{m.Employment}/probation/extend", Json(new { extendedTo = "2026-12-31", rowVersion = m.ProbationVersion })),
            "POST /api/hrm/employments/{id:guid}/probation/confirm" => ($"/api/hrm/employments/{m.Employment}/probation/confirm", Json(new { rowVersion = m.EmploymentVersion })),
            "POST /api/hrm/employments/{id:guid}/probation/not-confirm" => ($"/api/hrm/employments/{m.Employment}/probation/not-confirm", Json(new { rowVersion = m.ProbationVersion })),

            // S1.20d: payment accounts (a change is held for a second person: 202) and statutory registrations of A's employment.
            "GET /api/hrm/employments/{id:guid}/bank-accounts" => ($"/api/hrm/employments/{m.Employment}/bank-accounts", null),
            "POST /api/hrm/employments/{id:guid}/bank-accounts" => ($"/api/hrm/employments/{m.Employment}/bank-accounts", Json(new
            {
                changes = new[] { new { action = "add", paymentMode = "cheque", splitKind = "percent", splitValue = 10m, priority = 5 } },
                reason = "probe",
            })),
            "GET /api/hrm/bank-accounts/{id:guid}/reveal" => ($"/api/hrm/bank-accounts/{m.BankAccount}/reveal", null),
            "GET /api/hrm/employments/{id:guid}/statutory-registrations" => ($"/api/hrm/employments/{m.Employment}/statutory-registrations", null),
            "POST /api/hrm/employments/{id:guid}/statutory-registrations" => ($"/api/hrm/employments/{m.Employment}/statutory-registrations", Json(new
            {
                scheme = "pf" + string.Concat(Enumerable.Range(0, 6).Select(_ => (char)('a' + Random.Shared.Next(26)))), isApplicable = true,
            })),
            "PUT /api/hrm/statutory-registrations/{id:guid}" => ($"/api/hrm/statutory-registrations/{m.Registration}", Json(new { registrationNo = "PROBE-1", rowVersion = m.RegistrationVersion })),
            "DELETE /api/hrm/statutory-registrations/{id:guid}" => ($"/api/hrm/statutory-registrations/{m.Registration}?rowVersion={m.RegistrationVersion}", null),
            "GET /api/hrm/statutory-registrations/{id:guid}/reveal" => ($"/api/hrm/statutory-registrations/{m.Registration}/reveal", null),

            // S1.27a: punches of the token user's own employment, a kiosk punch for a mapped badge, a QR scan of A's location, A's QR code, the events batch of A's employment.
            "POST /api/hrm/attendance/events" => ("/api/hrm/attendance/events", Json(new
            {
                events = new[] { new { employmentId = m.Employment, eventAt = DateTimeOffset.UtcNow.AddHours(-1), direction = "in", source = "hr_entry", idempotencyKey = "probe-" + Guid.NewGuid().ToString("N") } },
            })),
            "POST /api/hrm/attendance/punch" => ("/api/hrm/attendance/punch", Json(new { direction = "in", idempotencyKey = "probe-" + m.Punching, companyId = m.Org.Company })),
            "POST /api/hrm/attendance/qr-punch" => ("/api/hrm/attendance/qr-punch", Json(new { code = m.QrCode, direction = "in", idempotencyKey = "probe-qr-" + m.Punching })),
            "POST /api/hrm/attendance/kiosk/punch" => ("/api/hrm/attendance/kiosk/punch", Json(new { companyId = m.Org.Company, badgeNo = m.Badge, direction = "in", idempotencyKey = "probe-kiosk-" + m.Punching })),
            "GET /api/hrm/attendance/my-day" => ("/api/hrm/attendance/my-day", null),
            "GET /api/hrm/attendance/qr-code" => ($"/api/hrm/attendance/qr-code?companyId={m.Org.Company}&locationId={m.Location}", null),
            var key => throw new InvalidOperationException($"No probe target for HR endpoint {key}; extend HrmProbe."),
        };
    }

    /// <summary>A hire of a new person (S1.20b) into the org unit and branch.</summary>
    private static object HireBody(Hire org, string name) => new
    {
        person = new { firstName = name + Guid.NewGuid().ToString("N")[..8], gender = "unspecified" },
        companyId = org.Company,
        employeeType = "permanent",
        joiningDate = "2024-01-01",
        assignment = new { orgUnitId = org.OrgUnit, branchId = org.Branch, title = "Probe officer" },
    };

    /// <summary>A transfer of the probe's first employment to the customer's second company (S1.20c).</summary>
    private static object Transfer(Material m) => new
    {
        toCompanyId = m.To.Company,
        effectiveDate = "2025-07-01",
        assignment = new { orgUnitId = m.To.OrgUnit, branchId = m.To.Branch, title = "Transferred probe" },
        rowVersion = m.EmploymentVersion,
    };

    /// <summary>A change that applies at once (acting charges need no approval by default), dated after the probe's joining date.</summary>
    private static object Acting(long rowVersion) => new
    {
        kind = "acting", reasonCode = "acting_start", effectiveDate = "2025-06-01", assignment = new { title = "Acting probe lead" }, rowVersion,
    };

    /// <summary>
    /// Where a customer's probe hires: the seeded org unit row (its company and that company's default branch, rule SC-2), and two
    /// fresh users of that customer (one linked to the first employment, one free to link). Written directly: the isolation host
    /// has no organisation set-up of its own to call.
    /// </summary>
    private async Task<(Hire Org, Guid Linked, Guid Free)> HireSetupAsync(string code, CancellationToken ct)
    {
        var org = await OrgAsync(code, ct);
        await using var c = await fx.OpenAdminAsync(code, ct);
        var (linked, free) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var n = Guid.NewGuid().ToString("N")[..10];
        await using (var command = new NpgsqlCommand("""
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $3, $3 || '@probe.test', $3, 'en', 'Asia/Karachi', false, 'active', now(), $4, now(), $4),
                   ($2, $3 || 'f', $3 || 'f@probe.test', $3, 'en', 'Asia/Karachi', false, 'active', now(), $4, now(), $4)
            """, c))
        {
            foreach (var v in new object[] { linked, free, "hp" + n, BaselineSeed.SystemUserId })
            {
                command.Parameters.Add(new NpgsqlParameter { Value = v });
            }

            await command.ExecuteNonQueryAsync(ct);
        }

        return (org, linked, free);
    }

    /// <summary>A fresh org unit row (with its company) and that company's default branch (rule SC-2).</summary>
    private async Task<Hire> OrgAsync(string code, CancellationToken ct)
    {
        var unit = await fx.RowAsync(code, IsolationFixture.Table("org.org_units"), ct);
        var company = (Guid)unit["company_id"]!;
        await using var c = await fx.OpenAdminAsync(code, ct);
        Guid branch;
        await using (var command = new NpgsqlCommand("select id from org.branches where company_id = $1 order by created_at limit 1", c))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = company });
            branch = (Guid)(await command.ExecuteScalarAsync(ct))!;
        }

        return new Hire(company, (Guid)unit["id"]!, branch);
    }

    /// <summary>
    /// A second company of the customer (S1.20c transfers) with an org unit, made through its own API; the company gets its default
    /// branch from rule SC-2. The seeded rows of the fixture all share one company, so this one is made separately.
    /// </summary>
    private async Task<Hire> SecondCompanyAsync(string host, string token, string code, CancellationToken ct)
    {
        var seeded = await fx.RowAsync(code, IsolationFixture.Table("org.org_units"), ct);
        var n = "TR" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var company = (await PostAsync(host, token, "/api/org/companies", new
        {
            code = n, nameI18n = new { en = "Company " + n }, legalName = "Company " + n + " Limited", countryCode = "PK", baseCurrencyCode = "PKR",
            defaultLanguage = "en", costCenterModel = "none", status = "active", isActive = true,
        }, ct)).GetProperty("id").GetGuid();
        var unit = (await PostAsync(host, token, "/api/org/org-units", new { companyId = company, code = n, nameI18n = new { en = "Unit " + n }, unitTypeId = (Guid)seeded["unit_type_id"]!, isActive = true }, ct)).GetProperty("id").GetGuid();
        await using var c = await fx.OpenAdminAsync(code, ct);
        await using var command = new NpgsqlCommand("select id from org.branches where company_id = $1 order by created_at limit 1", c);
        command.Parameters.Add(new NpgsqlParameter { Value = company });
        return new Hire(company, unit, (Guid)(await command.ExecuteScalarAsync(ct))!);
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

        // S1.20b: two employments of new persons, the first linked to a user of this customer.
        var (org, linkedUser, freeUser) = await HireSetupAsync(host == fx.HostA ? fx.CodeA : fx.CodeB, ct);
        var hired = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Employed"), ct)).GetProperty("employment");
        var employment = hired.GetProperty("id").GetGuid();
        var linked = await SendAsync(host, token, HttpMethod.Put, $"/api/hrm/employments/{employment}/user", Json(new { userId = linkedUser, rowVersion = hired.GetProperty("rowVersion").GetInt64() }), hints: false, ct);
        var linkedVersion = linked.Status == HttpStatusCode.OK ? JsonDocument.Parse(linked.Body).RootElement.GetProperty("rowVersion").GetInt64()
            : throw new InvalidOperationException($"HR probe material user link: {(int)linked.Status} {Short(linked.Body)}");
        var second = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Unlinked"), ct)).GetProperty("employment");

        // S1.20c: a second company to transfer to; a former employment (its separation written directly: separations are S1.25's);
        // a contract of the first employment; its probation (permanent employees start with one by default).
        var code = host == fx.HostA ? fx.CodeA : fx.CodeB;
        var to = await SecondCompanyAsync(host, token, code, ct);
        var former = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Former"), ct)).GetProperty("employment").GetProperty("id").GetGuid();
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            await using var command = new NpgsqlCommand("update hrm.employments set status = 'separated', separation_date = '2024-12-31', row_version = row_version + 1 where id = $1", c);
            command.Parameters.Add(new NpgsqlParameter { Value = former });
            await command.ExecuteNonQueryAsync(ct);
        }

        var contract = await PostAsync(host, token, $"/api/hrm/employments/{employment}/contracts", new { startDate = "2024-01-01", endDate = "2024-12-31", terms = new { hours = "40" } }, ct);
        var probation = (await GetAsync(host, token, $"/api/hrm/probation-records?employmentId={employment}", ct)).GetProperty("items")[0];

        // S1.20d: a cash payment account (written directly: a change through the API waits for a second person) and an EOBI registration.
        var account = Guid.CreateVersion7();
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            await using var command = new NpgsqlCommand("""
                insert into hrm.employee_bank_accounts (id, company_id, employment_id, payment_mode, split_kind, priority, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, company_id, id, 'cash', 'remainder', 1, '2024-01-01', now(), $2, now(), $2 from hrm.employments where id = $3
                """, c);
            command.Parameters.Add(new NpgsqlParameter { Value = account });
            command.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
            command.Parameters.Add(new NpgsqlParameter { Value = employment });
            await command.ExecuteNonQueryAsync(ct);
        }

        var registration = await PostAsync(host, token, $"/api/hrm/employments/{employment}/statutory-registrations", new { scheme = "eobi", isApplicable = true, registrationNo = "EOBI-" + cnic[..7], validFrom = "2024-01-01" }, ct);
        // S1.27a: a third employment linked to the probe's own token user (web and QR punches), a badge number mapped for all devices (kiosk), A's location and its QR code.
        var punching = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Punching"), ct)).GetProperty("employment");
        var badge = "PB" + Guid.NewGuid().ToString("N")[..8];
        Guid location;
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            // The token's own user cannot be linked through the API (a user cannot link themselves, FR-HRM-EMP-17), so the link is written directly.
            await using (var unlink = new NpgsqlCommand("update hrm.employments set user_ref = null where user_ref = $1", c))
            {
                unlink.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId }); // the material is made again for every endpoint
                await unlink.ExecuteNonQueryAsync(ct);
            }

            await using (var link = new NpgsqlCommand("update hrm.employments set user_ref = $1, row_version = row_version + 1 where id = $2", c))
            {
                link.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
                link.Parameters.Add(new NpgsqlParameter { Value = punching.GetProperty("id").GetGuid() });
                await link.ExecuteNonQueryAsync(ct);
            }

            await using (var command = new NpgsqlCommand("""
                insert into hrm.device_user_maps (id, company_id, device_id, device_user_no, employment_id, valid_from, created_at, created_by, updated_at, updated_by)
                values ($1, $2, null, $3, $4, '2024-01-01', now(), $5, now(), $5)
                """, c))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
                command.Parameters.Add(new NpgsqlParameter { Value = org.Company });
                command.Parameters.Add(new NpgsqlParameter { Value = badge });
                command.Parameters.Add(new NpgsqlParameter { Value = punching.GetProperty("id").GetGuid() });
                command.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
                await command.ExecuteNonQueryAsync(ct);
            }

        }

        var seededLocation = await fx.RowAsync(code, IsolationFixture.Table("org.locations"), ct); // the fixture's own location, of the same company as the org unit
        location = (Guid)seededLocation["id"]!;
        if ((Guid)seededLocation["company_id"]! != org.Company)
        {
            throw new InvalidOperationException("HR probe material: the seeded location and org unit belong to different companies.");
        }

        var shown = await GetAsync(host, token, $"/api/hrm/attendance/qr-code?companyId={org.Company}&locationId={location}", ct);
        return new Material(person, created.GetProperty("rowVersion").GetInt64(), into, identifier.Id, identifier.Version, contact.Id, contact.Version,
            address.Id, address.Version, relation.Id, relation.Version, qualification.Id, qualification.Version,
            match.GetProperty("id").GetGuid(), match.GetProperty("rowVersion").GetInt64(), created.GetProperty("personNo").GetString()!,
            org, employment, linkedVersion, second.GetProperty("id").GetGuid(), second.GetProperty("rowVersion").GetInt64(), freeUser,
            to, former, contract.GetProperty("id").GetGuid(), contract.GetProperty("rowVersion").GetInt64(), probation.GetProperty("rowVersion").GetInt64(),
            account, registration.GetProperty("id").GetGuid(), registration.GetProperty("rowVersion").GetInt64(),
            punching.GetProperty("id").GetGuid(), location, shown.GetProperty("code").GetString()!, badge);
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
