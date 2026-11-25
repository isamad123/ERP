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
/// payment account and a statutory registration of the first employment; from S1.23a a probation policy assigned to permanent employees
/// (made before the hires, so the first employment's probation comes from it) and a second policy with a draft; from S1.34a a pay group
/// with the periods of a tax year; from S1.32a a leave type and an opening balance of the first employment; from S1.32b a leave policy, a draft and an approved request, a year-end preview and the former employment moved to the second company; from S1.29b an attendance policy, a processed day with a worked-out overtime entry and a penalty (S1.29a's engine routes need nothing more: the first employment's days are processed, read and explained by id, the exception queue lists B's own); from S1.31 an employment in the pay group, a pay period locked by a calculated run and a waiting adjustment. Then, as for
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
    private static readonly string PushSecret = "probe-" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<Guid, string> pushSecrets = [];

    private sealed record Material(
        Guid Person, long PersonVersion, Guid Into, Guid Identifier, long IdentifierVersion, Guid Contact, long ContactVersion, Guid Address, long AddressVersion,
        Guid Relation, long RelationVersion, Guid Qualification, long QualificationVersion, Guid Case, long CaseVersion, string PersonNo,
        Hire Org, Guid Employment, long EmploymentVersion, Guid Unlinked, long UnlinkedVersion, Guid FreeUser,
        Hire To, Guid Former, Guid Contract, long ContractVersion, long ProbationVersion, Guid BankAccount, Guid Registration, long RegistrationVersion,
        Guid Position, long PositionVersion,
        Guid Policy, long PolicyVersion, Guid DraftPolicy, long DraftPolicyVersion, Guid Draft, long DraftVersion, DateOnly SetFrom,
        Guid Punching, Guid Location, string QrCode, string Badge,
        Guid PayGroup, long PayGroupVersion, Guid Period, long PeriodVersion, string PeriodCutOff,
        Guid Shift, long ShiftVersion, Guid Roster, long RosterVersion,
        Guid Component, long ComponentVersion, Guid Structure, long StructureVersion,
        Guid PushDevice, long PushDeviceVersion, Guid PullDevice, Guid Map, long MapVersion, Guid RemovalMap, long RemovalMapVersion, Guid Unmapped, long UnmappedVersion, string MapNo,
        Guid LeaveType, long LeaveTypeVersion, Guid LeaveEntry,
        Guid LeaveDraft, long LeaveDraftVersion, Guid LeaveApproved, Guid YearEndRun, Guid ToLeaveType, Guid Mover,
        Guid Overtime, Guid Penalty, Guid LockedPeriod, Guid LockRun, Guid Adjustment, Guid LockedEmployment, DateOnly LockedStart);

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
        var markers = new[] { a.Person, a.Into, a.Identifier, a.Contact, a.Address, a.Relation, a.Qualification, a.Case, a.Employment, a.Unlinked, a.Former, a.Contract, a.To.Company, a.BankAccount, a.Registration, a.Position, a.Policy, a.DraftPolicy, a.Draft, a.PayGroup, a.Period, a.Punching, a.Location, a.Shift, a.Roster, a.PushDevice, a.PullDevice, a.Map, a.RemovalMap, a.Unmapped, a.LeaveType, a.LeaveEntry, a.LeaveDraft, a.LeaveApproved, a.YearEndRun, a.ToLeaveType, a.Mover, a.Component, a.Structure, a.Overtime, a.Penalty, a.LockedPeriod, a.LockRun, a.Adjustment, a.LockedEmployment }.Select(g => g.ToString()).ToList(); // person numbers repeat across customers, so ids only
        var method = new HttpMethod(endpoint.Method);
        var (path, body) = Target(endpoint, a);
        pushSecrets[a.PushDevice] = PushSecret;
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
        else if (endpoint.Pattern == "/api/hrm/duplicates" && !await QueueListsAsync(fx.HostA, tokenA, own.Body, a.Case, ct))
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

            // S1.22a positions: a position in A's company and unit, actions on A's position, the strength exception of A's employment.
            "POST /api/hrm/positions" => ("/api/hrm/positions", Json(new { companyId = m.Org.Company, positionCode = "POS-" + Guid.NewGuid().ToString("N")[..10], title = "Probe post", orgUnitId = m.Org.OrgUnit, validFrom = "2020-01-01" })),
            "POST /api/hrm/positions/{id:guid}/changes" => ($"/api/hrm/positions/{m.Position}/changes", Json(new { effectiveDate = "2024-01-01", title = "Changed post", rowVersion = m.PositionVersion })),
            "POST /api/hrm/positions/{id:guid}/close" => ($"/api/hrm/positions/{m.Position}/close", Json(new { effectiveDate = "2030-01-01", rowVersion = m.PositionVersion })),
            "GET /api/hrm/positions/{id:guid}/holders" => ($"/api/hrm/positions/{m.Position}/holders", null),
            "GET /api/hrm/org-model" => ("/api/hrm/org-model", null),
            "GET /api/hrm/strength-report" => ($"/api/hrm/strength-report?companyId={m.Org.Company}", null),
            "GET /api/hrm/strength-report/drill" => ($"/api/hrm/strength-report/drill?companyId={m.Org.Company}&unitId={m.Org.OrgUnit}", null),
            "GET /api/hrm/strength-report/export" => ($"/api/hrm/strength-report/export?companyId={m.Org.Company}", null),
            "GET /api/hrm/org-chart" => ($"/api/hrm/org-chart?companyId={m.Org.Company}", null),
            "POST /api/hrm/org-chart/export" => ("/api/hrm/org-chart/export", Json(new { companyId = m.Org.Company, format = "csv" })),
            "POST /api/hrm/strength-exceptions" => ("/api/hrm/strength-exceptions", Json(new { employmentId = m.Employment, orgUnitId = m.Org.OrgUnit, effectiveDate = "2025-06-01", reason = "Probe cover" })),
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

            // S1.23a: the policy framework (A's policy, draft, company and employment).
            "GET /api/hrm/policy-types" => ("/api/hrm/policy-types", null),
            "POST /api/hrm/policies" => ("/api/hrm/policies", Json(new { companyId = m.Org.Company, policyType = "probation", code = "PB" + Guid.NewGuid().ToString("N")[..8], name = new { en = "Probe" }, parameters = new { months = 2 } })),
            "GET /api/hrm/policies/{id:guid}/detail" => ($"/api/hrm/policies/{m.Policy}/detail", null),
            "PUT /api/hrm/policies/{id:guid}/name" => ($"/api/hrm/policies/{m.Policy}/name", Json(new { name = new { en = "Renamed" }, rowVersion = m.PolicyVersion })),
            "POST /api/hrm/policies/{id:guid}/versions" => ($"/api/hrm/policies/{m.Policy}/versions", Json(new { parameters = new { months = 4 } })),
            "POST /api/hrm/policies/{id:guid}/retire" => ($"/api/hrm/policies/{m.DraftPolicy}/retire", Json(new { effectiveFrom = "2025-01-01", rowVersion = m.DraftPolicyVersion })),
            "PUT /api/hrm/policy-versions/{id:guid}/parameters" => ($"/api/hrm/policy-versions/{m.Draft}/parameters", Json(new { parameters = new { bands = new[] { new { serviceMonthsFrom = 0, days = 60 } } }, rowVersion = m.DraftVersion })),
            "DELETE /api/hrm/policy-versions/{id:guid}/draft" => ($"/api/hrm/policy-versions/{m.Draft}/draft?rowVersion={m.DraftVersion}", null),
            "POST /api/hrm/policy-versions/{id:guid}/activate" => ($"/api/hrm/policy-versions/{m.Draft}/activate", Json(new { effectiveFrom = "2024-01-01", rowVersion = m.DraftVersion })),
            "GET /api/hrm/policy-assignment-sets" => ($"/api/hrm/policy-assignment-sets?companyId={m.Org.Company}&policyType=probation", null),
            "POST /api/hrm/policy-assignment-sets/check" => ("/api/hrm/policy-assignment-sets/check", Json(SetBody(m))),
            "POST /api/hrm/policy-assignment-sets/activate" => ("/api/hrm/policy-assignment-sets/activate", Json(SetBody(m))),
            "GET /api/hrm/policy-resolution" => ($"/api/hrm/policy-resolution?employmentId={m.Employment}&policyType=probation", null),
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
            // S1.34a: pay groups, the periods of a tax year, the calendar and the readiness checklist of A's group and period.
            "POST /api/hrm/pay-groups" => ("/api/hrm/pay-groups", Json(new
            {
                companyId = m.Org.Company, code = "PG" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe group" }, frequency = "monthly", taxYearStartMonth = 7,
            })),
            "PUT /api/hrm/pay-groups/{id:guid}" => ($"/api/hrm/pay-groups/{m.PayGroup}", Json(new { cutOffDay = 2, rowVersion = m.PayGroupVersion })),
            "POST /api/hrm/pay-groups/{id:guid}/periods/generate" => ($"/api/hrm/pay-groups/{m.PayGroup}/periods/generate", Json(new { taxYear = 2030 })),
            "PUT /api/hrm/pay-periods/{id:guid}" => ($"/api/hrm/pay-periods/{m.Period}", Json(new { cutOffDate = m.PeriodCutOff, rowVersion = m.PeriodVersion })),
            "GET /api/hrm/pay-calendar" => ("/api/hrm/pay-calendar", null),
            "GET /api/hrm/pay-periods/{id:guid}/readiness" => ($"/api/hrm/pay-periods/{m.Period}/readiness", null),
            // S1.34b: components with versions, formula check, salary structures, and the compensation of A's employment.
            "POST /api/hrm/pay-components" => ("/api/hrm/pay-components", Json(new { companyId = m.Org.Company, code = "C" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe component" }, kind = "earning", payslipOrder = 1 })),
            "PUT /api/hrm/pay-components/{id:guid}" => ($"/api/hrm/pay-components/{m.Component}", Json(new { nameI18n = new { en = "Renamed probe component" }, rowVersion = m.ComponentVersion })),
            "POST /api/hrm/pay-components/{id:guid}/versions" => ($"/api/hrm/pay-components/{m.Component}/versions", Json(new { validFrom = "2030-01-01", method = "fixed", proRataRule = "none", statutoryFlags = new { taxable = true } })),
            "GET /api/hrm/pay-components/{id:guid}/detail" => ($"/api/hrm/pay-components/{m.Component}/detail", null),
            "POST /api/hrm/pay-formulas/check" => ("/api/hrm/pay-formulas/check", Json(new { companyId = m.Org.Company, formula = "1 + 1" })),
            "POST /api/hrm/salary-structures" => ("/api/hrm/salary-structures", Json(new { companyId = m.Org.Company, code = "S" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe structure" } })),
            "PUT /api/hrm/salary-structures/{id:guid}" => ($"/api/hrm/salary-structures/{m.Structure}", Json(new { nameI18n = new { en = "Renamed probe structure" }, rowVersion = m.StructureVersion })),
            "GET /api/hrm/salary-structures/{id:guid}/detail" => ($"/api/hrm/salary-structures/{m.Structure}/detail", null),
            "POST /api/hrm/employments/{id:guid}/compensation" => ($"/api/hrm/employments/{m.Employment}/compensation", Json(new { structureId = m.Structure, validFrom = "2030-01-01", reasonCode = "initial_pay", lines = new[] { new { componentId = m.Component, amount = 1m } } })),
            "GET /api/hrm/employments/{id:guid}/compensation" => ($"/api/hrm/employments/{m.Employment}/compensation", null),
            // S1.28a: shifts, the weekly-off days of A's employment, and A's draft roster with one working day.
            "POST /api/hrm/shifts" => ("/api/hrm/shifts", Json(new
            {
                companyId = m.Org.Company, code = "SH" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe shift" }, shiftType = "fixed", startTime = "08:00", endTime = "16:00", crossesMidnight = false, minFullDayMinutes = 420,
            })),
            "PUT /api/hrm/shifts/{id:guid}" => ($"/api/hrm/shifts/{m.Shift}", Json(new { nameI18n = new { en = "Renamed probe shift" }, rowVersion = m.ShiftVersion })),
            "GET /api/hrm/employments/{id:guid}/weekly-off" => ($"/api/hrm/employments/{m.Employment}/weekly-off?from=2030-03-04&to=2030-03-10", null),
            "POST /api/hrm/rosters" => ("/api/hrm/rosters", Json(new { companyId = m.Org.Company, name = "Probe roster", orgUnitId = m.Org.OrgUnit, periodStart = "2030-04-01", periodEnd = "2030-04-07" })),
            "PUT /api/hrm/rosters/{id:guid}" => ($"/api/hrm/rosters/{m.Roster}", Json(new { name = "Renamed probe roster", rowVersion = m.RosterVersion })),
            "POST /api/hrm/rosters/{id:guid}/assign" => ($"/api/hrm/rosters/{m.Roster}/assign", Json(new { shiftId = m.Shift, employmentIds = new[] { m.Employment } })),
            "POST /api/hrm/rosters/{id:guid}/copy-forward" => ($"/api/hrm/rosters/{m.Roster}/copy-forward", Json(new { })),
            "PUT /api/hrm/rosters/{id:guid}/days" => ($"/api/hrm/rosters/{m.Roster}/days", Json(new { changes = new[] { new { employmentId = m.Employment, workDate = "2030-03-05", dayType = "weekly_off" } } })),
            "GET /api/hrm/rosters/{id:guid}/checks" => ($"/api/hrm/rosters/{m.Roster}/checks", null),
            "POST /api/hrm/rosters/{id:guid}/publish" => ($"/api/hrm/rosters/{m.Roster}/publish", Json(new { rowVersion = m.RosterVersion })),
            // S1.28b: holidays with Hijri dates, an employment's holidays and optional-holiday choice, and the effective shift of a day.
            "GET /api/hrm/holidays" => ($"/api/hrm/holidays?companyId={m.Org.Company}&year=2030", null),
            "GET /api/hrm/employments/{id:guid}/holidays" => ($"/api/hrm/employments/{m.Employment}/holidays?year=2030", null),
            "PUT /api/hrm/employments/{id:guid}/optional-holidays" => ($"/api/hrm/employments/{m.Employment}/optional-holidays", Json(new { year = 2030, holidayIds = Array.Empty<Guid>() })),
            "GET /api/hrm/shifts/effective" => ($"/api/hrm/shifts/effective?employment={m.Employment}&date=2030-03-04", null),
            // S1.27b: devices, the signed push, device-user mapping, removal tasks, unmapped punches and the punch log.
            "GET /api/hrm/attendance/devices" => ($"/api/hrm/attendance/devices?companyId={m.Org.Company}", null),
            "POST /api/hrm/attendance/devices" => ("/api/hrm/attendance/devices", Json(new { companyId = m.Org.Company, locationId = m.Location, serialNo = "SN" + Guid.NewGuid().ToString("N")[..10], provider = "generic_push", credentials = PushSecret })),
            "PUT /api/hrm/attendance/devices/{id:guid}" => ($"/api/hrm/attendance/devices/{m.PushDevice}", Json(new { model = "Probe model", rowVersion = m.PushDeviceVersion })),
            "POST /api/hrm/attendance/devices/{id:guid}/sync" => ($"/api/hrm/attendance/devices/{m.PullDevice}/sync", null),
            "POST /api/hrm/attendance/devices/{id:guid}/push" => ($"/api/hrm/attendance/devices/{m.PushDevice}/push", Json(new { records = new[] { new { userNo = m.MapNo, at = "2030-01-01T08:00:00+05:00", direction = "in" } } })),
            "GET /api/hrm/attendance/device-maps" => ($"/api/hrm/attendance/device-maps?companyId={m.Org.Company}", null),
            "POST /api/hrm/attendance/device-maps" => ("/api/hrm/attendance/device-maps", Json(new { companyId = m.Org.Company, deviceId = m.PushDevice, deviceUserNo = "P" + Guid.NewGuid().ToString("N")[..10], employmentId = m.Employment, consentDate = "2024-01-01" })),
            "POST /api/hrm/attendance/device-maps/{id:guid}/end" => ($"/api/hrm/attendance/device-maps/{m.Map}/end", Json(new { endDate = "2031-01-01", rowVersion = m.MapVersion })),
            "POST /api/hrm/attendance/device-maps/{id:guid}/removal-done" => ($"/api/hrm/attendance/device-maps/{m.RemovalMap}/removal-done", Json(new { rowVersion = m.RemovalMapVersion })),
            "GET /api/hrm/attendance/unmapped" => ($"/api/hrm/attendance/unmapped?companyId={m.Org.Company}", null),
            "POST /api/hrm/attendance/unmapped/{id:guid}/discard" => ($"/api/hrm/attendance/unmapped/{m.Unmapped}/discard", Json(new { rowVersion = m.UnmappedVersion })),
            "GET /api/hrm/attendance/punch-log" => ($"/api/hrm/attendance/punch-log?companyId={m.Org.Company}", null),
            "POST /api/hrm/leave-types" => ("/api/hrm/leave-types", Json(new
            {
                companyId = m.Org.Company, code = "LT" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe leave", ur = "آزمائشی رخصت" }, isPaid = true,
                counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false,
            })),
            "PUT /api/hrm/leave-types/{id:guid}" => ($"/api/hrm/leave-types/{m.LeaveType}", Json(new { requiresDocument = true, rowVersion = m.LeaveTypeVersion })),
            "POST /api/hrm/employments/{id:guid}/leave-ledger" => ($"/api/hrm/employments/{m.Employment}/leave-ledger", Json(new { leaveTypeId = m.LeaveType, kind = "grant", quantity = 1, entryDate = "2025-03-01", reason = "Probe grant" })),
            "POST /api/hrm/leave-ledger/{id:guid}/reverse" => ($"/api/hrm/leave-ledger/{m.LeaveEntry}/reverse", Json(new { reason = "Probe reversal" })),
            "GET /api/hrm/employments/{id:guid}/leave-balances" => ($"/api/hrm/employments/{m.Employment}/leave-balances?asOf=2024-06-30", null),
            "GET /api/hrm/my/leave-balances" => ("/api/hrm/my/leave-balances", null),
            "GET /api/hrm/employments/{id:guid}/leave-recompute" => ($"/api/hrm/employments/{m.Employment}/leave-recompute", null),
            // S1.32b: leave requests, the year-end's decisions and the leave side of a transfer.
            "POST /api/hrm/leave-requests/preview" => ("/api/hrm/leave-requests/preview", Json(new { employmentId = m.Employment, leaveTypeId = m.LeaveType, startDate = "2025-06-03" })),
            "POST /api/hrm/leave-requests" => ("/api/hrm/leave-requests", Json(new { employmentId = m.Employment, leaveTypeId = m.LeaveType, startDate = "2025-06-04", submit = false })),
            "PUT /api/hrm/leave-requests/{id:guid}/draft" => ($"/api/hrm/leave-requests/{m.LeaveDraft}/draft", Json(new { reason = "Probe change", rowVersion = m.LeaveDraftVersion })),
            "POST /api/hrm/leave-requests/{id:guid}/submit" => ($"/api/hrm/leave-requests/{m.LeaveDraft}/submit", Json(new { rowVersion = m.LeaveDraftVersion })),
            "POST /api/hrm/leave-requests/{id:guid}/cancel" => ($"/api/hrm/leave-requests/{m.LeaveApproved}/cancel", Json(new { reason = "Probe cancel" })),
            "POST /api/hrm/leave-requests/{id:guid}/change" => ($"/api/hrm/leave-requests/{m.LeaveApproved}/change", Json(new { startDate = "2025-07-07" })),
            "GET /api/hrm/leave-requests/{id:guid}/details" => ($"/api/hrm/leave-requests/{m.LeaveDraft}/details", null),
            "GET /api/hrm/leave-requests/{id:guid}/coverage" => ($"/api/hrm/leave-requests/{m.LeaveDraft}/coverage", null),
            "GET /api/hrm/leave-calendar" => ("/api/hrm/leave-calendar?from=2025-06-01&to=2025-06-30", null),
            "POST /api/hrm/leave-year-end-runs/{id:guid}/confirm" => ($"/api/hrm/leave-year-end-runs/{m.YearEndRun}/confirm", Json(new { reason = "Probe confirm" })),
            "POST /api/hrm/leave-year-end-runs/{id:guid}/rollback" => ($"/api/hrm/leave-year-end-runs/{m.YearEndRun}/rollback", Json(new { reason = "Probe roll back" })),
            "GET /api/hrm/employments/{id:guid}/leave-transfer" => ($"/api/hrm/employments/{m.Mover}/leave-transfer", null),
            "POST /api/hrm/employments/{id:guid}/leave-transfer" => ($"/api/hrm/employments/{m.Mover}/leave-transfer", Json(new { maps = new[] { new { fromLeaveTypeId = m.LeaveType, toLeaveTypeId = m.ToLeaveType } } })),
            "POST /api/hrm/employments/{id:guid}/attendance-days/process" => ($"/api/hrm/employments/{m.Employment}/attendance-days/process", Json(new { from = "2024-06-03", to = "2024-06-04" })),
            "GET /api/hrm/employments/{id:guid}/attendance-days" => ($"/api/hrm/employments/{m.Employment}/attendance-days?from=2024-06-01&to=2024-06-30", null),
            "GET /api/hrm/employments/{id:guid}/attendance-days/{date}/explain" => ($"/api/hrm/employments/{m.Employment}/attendance-days/2024-06-03/explain", null),
            "GET /api/hrm/attendance/exceptions" => ($"/api/hrm/attendance/exceptions?companyId={m.Org.Company}", null),
            "GET /api/hrm/attendance/dashboard" => ($"/api/hrm/attendance/dashboard?companyId={m.Org.Company}", null),
            "GET /api/hrm/attendance/dashboard/people" => ($"/api/hrm/attendance/dashboard/people?companyId={m.Org.Company}&status=present", null),
            "GET /api/hrm/attendance/reports" => ("/api/hrm/attendance/reports", null),
            "GET /api/hrm/attendance/reports/{code}" => ($"/api/hrm/attendance/reports/muster_daily?companyId={m.Org.Company}&from=2024-06-01&to=2024-06-30", null),
            // S1.30a: requests for a date long past; the probe's company has no policy or lock, so a request that got through would be created.
            "POST /api/hrm/attendance/regularization-requests" => ("/api/hrm/attendance/regularization-requests", Json(new { employmentId = m.Employment, workDate = "2024-06-03", requestedIn = "2024-06-03T09:00:00+05:00", reason = "Probe correction" })),
            "POST /api/hrm/attendance/duty-requests" => ("/api/hrm/attendance/duty-requests", Json(new { employmentId = m.Employment, kind = "field_duty", startDate = "2024-06-03", endDate = "2024-06-04", purpose = "Probe duty" })),
            // S1.29b: A's worked-out overtime entry and applied warning penalty (written directly: the engine makes them), and overtime asked for a day in 2030
            // under the attendance policy of the material (pre-approval on working days).
            "POST /api/hrm/attendance/overtime-entries/{id:guid}/claim" => ($"/api/hrm/attendance/overtime-entries/{m.Overtime}/claim", Json(new { reason = "Probe claim" })),
            "POST /api/hrm/attendance/overtime-requests" => ("/api/hrm/attendance/overtime-requests", Json(new { employmentId = m.Employment, workDate = "2030-03-05", minutes = 60, reason = "Probe overtime" })),
            "POST /api/hrm/attendance/penalties/{id:guid}/waiver" => ($"/api/hrm/attendance/penalties/{m.Penalty}/waiver", Json(new { reason = "Probe waiver" })),
            // S1.31: the lock console on A's July pay period (open, past its cut-off) and August period (locked by a calculated run), an adjustment of an
            // August day and A's waiting adjustment (both written directly in the material, standing in for payroll's calculation).
            "GET /api/hrm/attendance/periods" => ($"/api/hrm/attendance/periods?companyId={m.Org.Company}", null),
            "GET /api/hrm/attendance/periods/{id:guid}" => ($"/api/hrm/attendance/periods/{m.Period}", null),
            "POST /api/hrm/attendance/periods/{id:guid}/lock-request" => ($"/api/hrm/attendance/periods/{m.Period}/lock-request", Json(new { reason = "Probe lock", acceptOpenItems = true })),
            "POST /api/hrm/attendance/periods/{id:guid}/reopen-request" => ($"/api/hrm/attendance/periods/{m.LockedPeriod}/reopen-request", Json(new { reason = "Probe reopen" })),
            "GET /api/hrm/attendance/periods/{id:guid}/late-events" => ($"/api/hrm/attendance/periods/{m.LockedPeriod}/late-events", null),
            "POST /api/hrm/attendance/adjustments/impact" => ("/api/hrm/attendance/adjustments/impact", Json(new { employmentId = m.LockedEmployment, kind = "attendance", days = new[] { new { date = Iso(m.LockedStart.AddDays(5)), result = "present" } } })),
            "POST /api/hrm/attendance/adjustments" => ("/api/hrm/attendance/adjustments", Json(new { employmentId = m.LockedEmployment, kind = "attendance", reason = "Probe adjustment", days = new[] { new { date = Iso(m.LockedStart.AddDays(11)), result = "present" } } })),
            "GET /api/hrm/attendance/adjustments" => ($"/api/hrm/attendance/adjustments?companyId={m.Org.Company}", null),
            "GET /api/hrm/attendance/adjustments/{id:guid}" => ($"/api/hrm/attendance/adjustments/{m.Adjustment}", null),
            var key => throw new InvalidOperationException($"No probe target for HR endpoint {key}; extend HrmProbe."),
        };
    }

    /// <summary>A new set of A's probation assignments (S1.23a), on top of the material's set of 2020.</summary>
    private static readonly string[] Permanent = ["permanent"];

    private static object SetBody(Material m) => new
    {
        companyId = m.Org.Company, policyType = "probation", effectiveFrom = m.SetFrom.AddDays(1), basedOn = m.SetFrom,
        assignments = new[] { new { policyId = m.Policy, precedence = 1, rule = new { conditions = Array.Empty<object>() } } },
    };

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
        // The probe's own unit (not the shared seeded row the kernel suite deletes as its positive control): positions and people sit in it,
        // and a unit in use cannot be switched off (S1.22b).
        var unit = await fx.SeedAsync(code, IsolationFixture.Table("org.org_units"), ct);
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
        var match = (await OpenCasesAsync(host, token, null, ct)).Last(c => c.GetProperty("other").GetProperty("id").GetGuid() == person);
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

        // S1.23a: a probation policy (version 1 active, assigned to permanent employees from 2020) and a second policy left as a draft.
        var policy = await PostAsync(host, token, "/api/hrm/policies", new { companyId = org.Company, policyType = "probation", code = "PR" + Guid.NewGuid().ToString("N")[..8], name = new { en = "Probe probation" }, parameters = new { months = 3 } }, ct);
        var policyId = policy.GetProperty("id").GetGuid();
        var v1 = policy.GetProperty("versions")[0];
        await OkAsync(host, token, $"/api/hrm/policy-versions/{v1.GetProperty("id").GetGuid()}/activate", new { effectiveFrom = "2020-01-01", rowVersion = v1.GetProperty("rowVersion").GetInt64() }, ct);
        // The company is shared by every material of this customer: the new set starts a day after the newest one.
        var sets = await GetAsync(host, token, $"/api/hrm/policy-assignment-sets?companyId={org.Company}&policyType=probation", ct);
        DateOnly? latest = sets.GetProperty("latestFrom").ValueKind == JsonValueKind.String ? DateOnly.Parse(sets.GetProperty("latestFrom").GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
        var setFrom = (latest ?? new DateOnly(2019, 12, 31)).AddDays(1);
        await OkAsync(host, token, "/api/hrm/policy-assignment-sets/activate", new
        {
            companyId = org.Company, policyType = "probation", effectiveFrom = setFrom, basedOn = latest,
            assignments = new[] { new { policyId, precedence = 1, rule = new { conditions = new[] { new { attribute = "employee_type", @operator = "in", values = Permanent } } } } },
        }, ct);
        var policyVersion = (await GetAsync(host, token, $"/api/hrm/policies/{policyId}/detail", ct)).GetProperty("rowVersion").GetInt64();
        var draftPolicy = await PostAsync(host, token, "/api/hrm/policies", new { companyId = org.Company, policyType = "notice_period", code = "NP" + Guid.NewGuid().ToString("N")[..8], name = new { en = "Probe notice" }, parameters = new { bands = new[] { new { serviceMonthsFrom = 0, days = 30 } } } }, ct);
        var draft = draftPolicy.GetProperty("versions")[0];
        var hired = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Employed"), ct)).GetProperty("employment");
        var employment = hired.GetProperty("id").GetGuid();
        var linked = await SendAsync(host, token, HttpMethod.Put, $"/api/hrm/employments/{employment}/user", Json(new { userId = linkedUser, rowVersion = hired.GetProperty("rowVersion").GetInt64() }), hints: false, ct);
        var linkedVersion = linked.Status == HttpStatusCode.OK ? JsonDocument.Parse(linked.Body).RootElement.GetProperty("rowVersion").GetInt64()
            : throw new InvalidOperationException($"HR probe material user link: {(int)linked.Status} {Short(linked.Body)}");
        var second = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Unlinked"), ct)).GetProperty("employment");
        var position = await PostAsync(host, token, "/api/hrm/positions", new { companyId = org.Company, positionCode = "ISO-" + Guid.NewGuid().ToString("N")[..10], title = "Probe post", orgUnitId = org.OrgUnit, validFrom = "2020-01-01" }, ct);

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
        // S1.34a: a pay group of the probe's company with the periods of tax year 2024 (a monthly group starting in July).
        var group = await PostAsync(host, token, "/api/hrm/pay-groups", new { companyId = org.Company, code = "PG" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe group" }, frequency = "monthly", taxYearStartMonth = 7 }, ct);
        var groupId = group.GetProperty("id").GetGuid();
        await PostAsync(host, token, $"/api/hrm/pay-groups/{groupId}/periods/generate", new { taxYear = 2024 }, ct);
        var period = (await GetAsync(host, token, $"/api/hrm/pay-periods?payGroupId={groupId}", ct)).GetProperty("items")[0];
        // S1.34b: a component and a salary structure of the probe's company.
        var component = await PostAsync(host, token, "/api/hrm/pay-components", new { companyId = org.Company, code = "C" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe component" }, kind = "earning", payslipOrder = 1 }, ct);
        var structure = await PostAsync(host, token, "/api/hrm/salary-structures", new { companyId = org.Company, code = "S" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe structure" } }, ct);
        // The component list of a structure is held for a second person by default, so the one component of the probe's structure is written directly.
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            await using var insert = new NpgsqlCommand("""
                insert into hrm.salary_structure_components (id, structure_id, component_id, evaluation_order, is_mandatory, currency_code, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, $2, $3, 1, false, base_currency_code, date '2020-01-01', now(), $4, now(), $4 from org.companies where id = $5
                """, c);
            insert.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            insert.Parameters.Add(new NpgsqlParameter { Value = structure.GetProperty("id").GetGuid() });
            insert.Parameters.Add(new NpgsqlParameter { Value = component.GetProperty("id").GetGuid() });
            insert.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
            insert.Parameters.Add(new NpgsqlParameter { Value = org.Company });
            await insert.ExecuteNonQueryAsync(ct);
        }

        // S1.28a: a shift and a draft roster of the week of 4 March 2030 with one working day of the first employment.
        var shift = await PostAsync(host, token, "/api/hrm/shifts", new { companyId = org.Company, code = "SH" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe shift" }, shiftType = "fixed", startTime = "08:00", endTime = "16:00", crossesMidnight = false, minFullDayMinutes = 420 }, ct);
        var roster = await PostAsync(host, token, "/api/hrm/rosters", new { companyId = org.Company, name = "Probe week", orgUnitId = org.OrgUnit, periodStart = "2030-03-04", periodEnd = "2030-03-10" }, ct);
        await PutAsync(host, token, $"/api/hrm/rosters/{roster.GetProperty("id").GetGuid()}/days", new { changes = new[] { new { employmentId = employment, workDate = "2030-03-04", dayType = "working", shiftId = shift.GetProperty("id").GetGuid() } } }, ct);
        // S1.27b: the biometric switch of the company, a pushing and a pulling device at A's location, a consented mapping, a mapping with a removal task and an unmapped punch.
        await PutAsync(host, token, "/api/cfg/settings/hrm.att.biometric_enabled", new { layer = "company", scopeId = org.Company, value = true, reason = "probe" }, ct);
        var pushDevice = await PostAsync(host, token, "/api/hrm/attendance/devices", new { companyId = org.Company, locationId = location, serialNo = "SN" + Guid.NewGuid().ToString("N")[..10], provider = "generic_push", credentials = PushSecret }, ct);
        var pullDevice = await PostAsync(host, token, "/api/hrm/attendance/devices", new { companyId = org.Company, locationId = location, serialNo = "SN" + Guid.NewGuid().ToString("N")[..10], provider = "zkteco", host = "127.0.0.1", port = 9, protocol = "tcp", credentials = "123456" }, ct);
        var mapNo = "M" + Guid.NewGuid().ToString("N")[..10];
        var map = await PostAsync(host, token, "/api/hrm/attendance/device-maps", new { companyId = org.Company, deviceId = pushDevice.GetProperty("id").GetGuid(), deviceUserNo = mapNo, employmentId = employment, consentDate = "2024-01-01" }, ct);
        var mapRow = map.GetProperty("map");
        var removalMap = Guid.CreateVersion7();
        var unmapped = Guid.CreateVersion7();
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            await using (var command = new NpgsqlCommand("""
                insert into hrm.device_user_maps (id, company_id, device_id, device_user_no, employment_id, consent_at, valid_from, valid_to, removal_due_at, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, '2023-01-01', '2023-01-01', '2024-01-01', now(), now(), $6, now(), $6)
                """, c))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = removalMap });
                command.Parameters.Add(new NpgsqlParameter { Value = org.Company });
                command.Parameters.Add(new NpgsqlParameter { Value = pushDevice.GetProperty("id").GetGuid() });
                command.Parameters.Add(new NpgsqlParameter { Value = "R" + Guid.NewGuid().ToString("N")[..10] });
                command.Parameters.Add(new NpgsqlParameter { Value = employment });
                command.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
                await command.ExecuteNonQueryAsync(ct);
            }

            await using (var command = new NpgsqlCommand("""
                insert into hrm.unmapped_punches (id, company_id, device_id, device_user_no, punched_at, direction, idempotency_key, received_at, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, '2030-01-01T03:00:00Z', 'in', $5, now(), 'open', now(), $6, now(), $6)
                """, c))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = unmapped });
                command.Parameters.Add(new NpgsqlParameter { Value = org.Company });
                command.Parameters.Add(new NpgsqlParameter { Value = pushDevice.GetProperty("id").GetGuid() });
                command.Parameters.Add(new NpgsqlParameter { Value = "U" + Guid.NewGuid().ToString("N")[..10] });
                command.Parameters.Add(new NpgsqlParameter { Value = "probe-" + Guid.NewGuid().ToString("N") });
                command.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        // S1.32a: a leave type of the probe's company and an opening balance of the first employment.
        var leaveType = await PostAsync(host, token, "/api/hrm/leave-types", new
        {
            companyId = org.Company, code = "LV" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe annual", ur = "آزمائشی سالانہ" }, isPaid = true,
            counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false,
        }, ct);
        var leaveEntry = await PostAsync(host, token, $"/api/hrm/employments/{employment}/leave-ledger", new { leaveTypeId = leaveType.GetProperty("id").GetGuid(), kind = "opening", quantity = 2, entryDate = "2024-01-01", reason = "Probe opening" }, ct);

        // S1.32b: a leave policy listing the type (assigned to permanent employees, as the probation one), a draft request of the first employment, an
        // approved request with its day and a year-end preview (written directly: the approval is the workflow's, the preview the job's), and a separated
        // employment moved to the second company (written directly: transfers are S1.20c's) with a balance of a type the new company does not have.
        var leaveTypeId = leaveType.GetProperty("id").GetGuid();
        var leavePolicy = await PostAsync(host, token, "/api/hrm/policies", new
        {
            companyId = org.Company, policyType = "leave", code = "LP" + Guid.NewGuid().ToString("N")[..8], name = new { en = "Probe leave policy" },
            parameters = new { types = new[] { new { leaveType = leaveType.GetProperty("code").GetString(), entitlement = new { model = "manual" }, negativeLimit = 100 } } },
        }, ct);
        var lv1 = leavePolicy.GetProperty("versions")[0];
        await OkAsync(host, token, $"/api/hrm/policy-versions/{lv1.GetProperty("id").GetGuid()}/activate", new { effectiveFrom = "2020-01-01", rowVersion = lv1.GetProperty("rowVersion").GetInt64() }, ct);
        var leaveSets = await GetAsync(host, token, $"/api/hrm/policy-assignment-sets?companyId={org.Company}&policyType=leave", ct);
        DateOnly? leaveLatest = leaveSets.GetProperty("latestFrom").ValueKind == JsonValueKind.String ? DateOnly.Parse(leaveSets.GetProperty("latestFrom").GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
        await OkAsync(host, token, "/api/hrm/policy-assignment-sets/activate", new
        {
            companyId = org.Company, policyType = "leave", effectiveFrom = (leaveLatest ?? new DateOnly(2019, 12, 31)).AddDays(1), basedOn = leaveLatest,
            assignments = new[] { new { policyId = leavePolicy.GetProperty("id").GetGuid(), precedence = 1, rule = new { conditions = new[] { new { attribute = "employee_type", @operator = "in", values = Permanent } } } } },
        }, ct);
        var leaveDraft = await PostAsync(host, token, "/api/hrm/leave-requests", new { employmentId = employment, leaveTypeId, startDate = "2025-06-02", submit = false }, ct);
        var leaveApproved = Guid.CreateVersion7();
        var yearEndRun = Guid.CreateVersion7();
        var toLeaveType = (await PostAsync(host, token, "/api/hrm/leave-types", new
        {
            companyId = to.Company, code = "LT" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Probe leave there", ur = "آزمائشی رخصت" }, isPaid = true,
            counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false,
        }, ct)).GetProperty("id").GetGuid();
        var mover = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Mover"), ct)).GetProperty("employment").GetProperty("id").GetGuid();
        await PostAsync(host, token, $"/api/hrm/employments/{mover}/leave-ledger", new { leaveTypeId, kind = "opening", quantity = 3, entryDate = "2024-06-01", reason = "Probe opening" }, ct);
        Guid formerPerson;
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            await using (var separate = new NpgsqlCommand("update hrm.employments set status = 'separated', separation_date = '2024-12-31', row_version = row_version + 1 where id = $1", c))
            {
                separate.Parameters.Add(new NpgsqlParameter { Value = mover });
                await separate.ExecuteNonQueryAsync(ct);
            }

            await using var command = new NpgsqlCommand("select person_id from hrm.employments where id = $1", c);
            command.Parameters.Add(new NpgsqlParameter { Value = mover });
            formerPerson = (Guid)(await command.ExecuteScalarAsync(ct))!;
        }

        var rehired = await PostAsync(host, token, "/api/hrm/employments", new
        {
            personId = formerPerson, companyId = to.Company, employeeType = "permanent", joiningDate = "2025-01-01",
            assignment = new { orgUnitId = to.OrgUnit, branchId = to.Branch, title = "Probe officer" },
        }, ct);
        var moved = (rehired.TryGetProperty("employment", out var movedEmployment) ? movedEmployment : rehired).GetProperty("id").GetGuid();
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            async Task Exec(string sql, params object[] values)
            {
                await using var command = new NpgsqlCommand(sql, c);
                foreach (var value in values)
                {
                    command.Parameters.Add(new NpgsqlParameter { Value = value });
                }

                await command.ExecuteNonQueryAsync(ct);
            }

            var by = BaselineSeed.SystemUserId;
            await Exec("update hrm.employments set previous_employment_id = $2, row_version = row_version + 1 where id = $1", moved, mover);
            await Exec("""
                insert into hrm.employment_events (id, company_id, employment_id, kind, reason_code, effective_date, carry_over, changes, status, created_at, created_by, updated_at, updated_by)
                select $1, company_id, id, 'separation', 'transfer', '2025-01-01', '{"service":"keep","leave":"carry","loans":"carry"}'::jsonb,
                       jsonb_build_object('transfer', jsonb_build_object('to_employment_id', $2::text)), 'effective', now(), $4, now(), $4 from hrm.employments where id = $3
                """, Guid.CreateVersion7(), moved, mover, by);
            await Exec("""
                insert into hrm.leave_requests (id, company_id, request_no, employment_id, leave_type_id, start_date, end_date, start_part, end_part, days, file_ids, status, custom,
                                                created_at, created_by, updated_at, updated_by)
                values ($1, $2, 'PROBE-' || $1::text, $3, $4, '2025-05-05', '2025-05-05', 'full', 'full', 1, '{}', 'approved', '{}'::jsonb, now(), $5, now(), $5)
                """, leaveApproved, org.Company, employment, leaveTypeId, by);
            await Exec("""
                insert into hrm.leave_request_days (id, company_id, leave_request_id, employment_id, leave_date, quantity, is_paid, request_status, day_kind, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, '2025-05-05', 1, true, 'approved', 'leave', now(), $5, now(), $5)
                """, Guid.CreateVersion7(), org.Company, leaveApproved, employment, by);
            await Exec("""
                insert into hrm.leave_year_end_runs (id, company_id, leave_year, summary, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, 2023, jsonb_build_object('leaveYear', 2023, 'types', jsonb_build_array(jsonb_build_object('leaveTypeId', $3::text, 'code', 'X', 'yearStart', '2023-01-01', 'yearEnd', '2023-12-31')), 'lines', '[]'::jsonb),
                        'preview', now(), $4, now(), $4)
                """, yearEndRun, org.Company, leaveTypeId, by);
        }

        // S1.29b: an attendance policy for every employee (overtime on working days asked before the day), a processed day of the first employment in the
        // pay period of the material with a worked-out overtime entry and an applied warning penalty (written directly: the engine makes them).
        var attendance = await PostAsync(host, token, "/api/hrm/policies", new
        {
            companyId = org.Company, policyType = "attendance", code = "AT" + Guid.NewGuid().ToString("N")[..8], name = new { en = "Probe attendance" },
            parameters = new
            {
                lateGraceMinutes = 10, earlyGraceMinutes = 10, absentBelowMinutes = 120, breakMode = "none", roundingMode = "none", shiftWindowMinutes = 120,
                weeklyOffWork = "record", holidayWork = "record", overtime = new { working = new { multiplier = 1.5, approval = "pre", compensation = "pay", countBeforeShift = false } },
            },
        }, ct);
        var av1 = attendance.GetProperty("versions")[0];
        await OkAsync(host, token, $"/api/hrm/policy-versions/{av1.GetProperty("id").GetGuid()}/activate", new { effectiveFrom = "2020-01-01", rowVersion = av1.GetProperty("rowVersion").GetInt64() }, ct);
        var attendanceSets = await GetAsync(host, token, $"/api/hrm/policy-assignment-sets?companyId={org.Company}&policyType=attendance", ct);
        DateOnly? attendanceLatest = attendanceSets.GetProperty("latestFrom").ValueKind == JsonValueKind.String ? DateOnly.Parse(attendanceSets.GetProperty("latestFrom").GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
        await OkAsync(host, token, "/api/hrm/policy-assignment-sets/activate", new
        {
            companyId = org.Company, policyType = "attendance", effectiveFrom = (attendanceLatest ?? new DateOnly(2019, 12, 31)).AddDays(1), basedOn = attendanceLatest,
            assignments = new[] { new { policyId = attendance.GetProperty("id").GetGuid(), precedence = 1, rule = new { conditions = Array.Empty<object>() } } },
        }, ct);
        var overtime = Guid.CreateVersion7();
        var penalty = Guid.CreateVersion7();
        // S1.31: an employment of its own in the pay group (moving the others in or out of a group with an unfinished run would be refused).
        var lockedEmployment = (await PostAsync(host, token, "/api/hrm/employments", HireBody(org, "Locked"), ct)).GetProperty("employment").GetProperty("id").GetGuid();
        var lockRun = Guid.CreateVersion7();
        var lockedAttendance = Guid.CreateVersion7();
        var adjustment = Guid.CreateVersion7();
        Guid lockedPeriod;
        DateOnly lockedStart;
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            var by = BaselineSeed.SystemUserId;
            var periodId = Guid.CreateVersion7();
            var dayId = Guid.CreateVersion7();
            async Task Exec(string sql, params object[] values)
            {
                await using var command = new NpgsqlCommand(sql, c);
                foreach (var value in values)
                {
                    command.Parameters.Add(new NpgsqlParameter { Value = value });
                }

                await command.ExecuteNonQueryAsync(ct);
            }

            await Exec("""
                insert into hrm.attendance_periods (id, company_id, pay_group_id, pay_period_id, period_start, period_end, status, created_at, created_by, updated_at, updated_by)
                select $1, company_id, pay_group_id, id, start_date, end_date, 'open', now(), $3, now(), $3 from hrm.pay_periods where id = $2
                """, periodId, period.GetProperty("id").GetGuid(), by);
            await Exec("""
                insert into hrm.attendance_days (id, company_id, employment_id, work_date, day_type, result, worked_minutes, late_minutes, early_minutes, overtime_minutes,
                                                 explanation, policy_version_id, period_id, processed_at, created_at, created_by, updated_at, updated_by)
                select $1, $2, $3, p.period_start, 'working', 'late', 480, 30, 0, 60, '{"rule":"late"}'::jsonb, $4, p.id, now(), now(), $5, now(), $5
                from hrm.attendance_periods p where p.id = $6
                """, dayId, org.Company, employment, av1.GetProperty("id").GetGuid(), by, periodId);
            await Exec("""
                insert into hrm.overtime_entries (id, company_id, employment_id, work_date, day_type, minutes, rate_multiplier, attendance_day_id, status, created_at, created_by, updated_at, updated_by)
                select $1, company_id, employment_id, work_date, 'working', 60, 1.5, id, 'calculated', now(), $3, now(), $3 from hrm.attendance_days where id = $2
                """, overtime, dayId, by);
            await Exec("""
                insert into hrm.attendance_penalties (id, company_id, employment_id, work_date, kind, minutes, occurrence, action, attendance_day_id, period_id, policy_version_id,
                                                      explanation, status, created_at, created_by, updated_at, updated_by)
                select $1, company_id, employment_id, work_date, 'late', 30, 4, 'warning', id, period_id, policy_version_id, '{"key":"hrm.att.penalty.warning"}'::jsonb, 'applied',
                       now(), $3, now(), $3 from hrm.attendance_days where id = $2
                """, penalty, dayId, by);

            // S1.31: the material's own employment in the pay group; the first pay period other than the one above locked by a calculated run (S1.35 makes
            // runs), and a waiting adjustment of its fifth day carried into the next period.
            await Exec("update hrm.employment_assignments set pay_group_id = $1 where employment_id = $2", groupId, lockedEmployment);
            await Exec("""
                insert into hrm.pay_runs (id, company_id, run_no, pay_group_id, pay_period_id, kind, currency_code, stage_log, status, created_at, created_by, updated_at, updated_by)
                select $1, company_id, $2, pay_group_id, id, 'regular', 'PKR', '[]'::jsonb, 'calculated', now(), $3, now(), $3
                from hrm.pay_periods where pay_group_id = $4 and id <> $5 order by start_date limit 1
                """, lockRun, "PR-PROBE-" + Guid.NewGuid().ToString("N")[..8], by, groupId, period.GetProperty("id").GetGuid());
            await Exec("""
                insert into hrm.attendance_periods (id, company_id, pay_group_id, pay_period_id, period_start, period_end, status, locked_at, lock_kind, locked_by, locked_by_run_id,
                                                    created_at, created_by, updated_at, updated_by)
                select $1, r.company_id, r.pay_group_id, r.pay_period_id, p.start_date, p.end_date, 'locked', now(), 'payroll', $2, $3, now(), $2, now(), $2
                from hrm.pay_runs r join hrm.pay_periods p on p.id = r.pay_period_id where r.id = $3
                """, lockedAttendance, by, lockRun);
            await Exec("""
                insert into hrm.attendance_adjustments (id, company_id, employment_id, period_id, from_date, to_date, kind, old_summary, new_summary, reason, target_pay_period_id,
                                                        status_history, status, created_at, created_by, updated_at, updated_by)
                select $1, p.company_id, $2, $3, p.period_start + 4, p.period_start + 4, 'attendance', '{"days":[]}'::jsonb, '{"days":[]}'::jsonb, 'Probe material',
                       (select n.id from hrm.pay_periods n where n.pay_group_id = p.pay_group_id and n.start_date > p.period_end order by n.start_date limit 1),
                       '[{"status":"submitted","at":"2024-09-01T00:00:00.000000Z"}]'::jsonb, 'submitted', now(), $4, now(), $4
                from hrm.attendance_periods p where p.id = $3
                """, adjustment, lockedEmployment, lockedAttendance, by);
            await using var read = new NpgsqlCommand("select pay_period_id, period_start from hrm.attendance_periods where id = $1", c);
            read.Parameters.Add(new NpgsqlParameter { Value = lockedAttendance });
            await using var reader = await read.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            lockedPeriod = reader.GetGuid(0);
            lockedStart = DateOnly.FromDateTime(reader.GetDateTime(1));
        }

        return new Material(person, created.GetProperty("rowVersion").GetInt64(), into, identifier.Id, identifier.Version, contact.Id, contact.Version,
            address.Id, address.Version, relation.Id, relation.Version, qualification.Id, qualification.Version,
            match.GetProperty("id").GetGuid(), match.GetProperty("rowVersion").GetInt64(), created.GetProperty("personNo").GetString()!,
            org, employment, linkedVersion, second.GetProperty("id").GetGuid(), second.GetProperty("rowVersion").GetInt64(), freeUser,
            to, former, contract.GetProperty("id").GetGuid(), contract.GetProperty("rowVersion").GetInt64(), probation.GetProperty("rowVersion").GetInt64(),
            account, registration.GetProperty("id").GetGuid(), registration.GetProperty("rowVersion").GetInt64(),
            position.GetProperty("id").GetGuid(), position.GetProperty("rowVersion").GetInt64(),
            policyId, policyVersion, draftPolicy.GetProperty("id").GetGuid(), draftPolicy.GetProperty("rowVersion").GetInt64(), draft.GetProperty("id").GetGuid(), draft.GetProperty("rowVersion").GetInt64(), setFrom,
            punching.GetProperty("id").GetGuid(), location, shown.GetProperty("code").GetString()!, badge,
            groupId, group.GetProperty("rowVersion").GetInt64(), period.GetProperty("id").GetGuid(), period.GetProperty("rowVersion").GetInt64(), period.GetProperty("cutOffDate").GetString()!,
            shift.GetProperty("id").GetGuid(), shift.GetProperty("rowVersion").GetInt64(), roster.GetProperty("id").GetGuid(), roster.GetProperty("rowVersion").GetInt64(),
            component.GetProperty("id").GetGuid(), component.GetProperty("rowVersion").GetInt64(), structure.GetProperty("id").GetGuid(), structure.GetProperty("rowVersion").GetInt64(),
            pushDevice.GetProperty("id").GetGuid(), pushDevice.GetProperty("rowVersion").GetInt64(), pullDevice.GetProperty("id").GetGuid(), mapRow.GetProperty("id").GetGuid(), mapRow.GetProperty("rowVersion").GetInt64(),
            removalMap, 1, unmapped, 1, mapNo,
            leaveType.GetProperty("id").GetGuid(), leaveType.GetProperty("rowVersion").GetInt64(), leaveEntry.GetProperty("id").GetGuid(),
            leaveDraft.GetProperty("id").GetGuid(), leaveDraft.GetProperty("rowVersion").GetInt64(), leaveApproved, yearEndRun, toLeaveType, mover,
            overtime, penalty, lockedPeriod, lockRun, adjustment, lockedEmployment, lockedStart);
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task OkAsync(string host, string token, string path, object body, CancellationToken ct)
    {
        var reply = await SendAsync(host, token, HttpMethod.Post, path, Json(body), hints: false, ct);
        if (reply.Status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"HR probe material {path}: {(int)reply.Status} {Short(reply.Body)}");
        }
    }

    private async Task<JsonElement> PostAsync(string host, string token, string path, object body, CancellationToken ct)
    {
        var reply = await SendAsync(host, token, HttpMethod.Post, path, Json(body), hints: false, ct);
        return reply.Status == HttpStatusCode.Created ? JsonDocument.Parse(reply.Body).RootElement.Clone()
            : throw new InvalidOperationException($"HR probe material {path}: {(int)reply.Status} {Short(reply.Body)}");
    }

    private async Task PutAsync(string host, string token, string path, object body, CancellationToken ct)
    {
        var reply = await SendAsync(host, token, HttpMethod.Put, path, Json(body), hints: false, ct);
        if (reply.Status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"HR probe material {path}: {(int)reply.Status} {Short(reply.Body)}");
        }
    }

    /// <summary>
    /// The positive control of <c>GET /api/hrm/duplicates</c>: <paramref name="firstPage"/> is the endpoint's own answer to A; the case is
    /// looked for on it and then on the following pages (<c>next</c>). Every probe run adds an open case, so after 50 runs a case
    /// is no longer on the first page (the S1.23b review saw this fail locally).
    /// </summary>
    private async Task<bool> QueueListsAsync(string host, string token, string firstPage, Guid caseId, CancellationToken ct)
    {
        var first = JsonDocument.Parse(firstPage).RootElement.Clone();
        var cases = first.GetProperty("items").EnumerateArray().ToList();
        if (first.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
        {
            cases.AddRange(await OpenCasesAsync(host, token, next.GetGuid(), ct));
        }

        return cases.Exists(c => c.GetProperty("id").GetGuid() == caseId);
    }

    /// <summary>Every open duplicate case of the customer from <paramref name="after"/> on, following <c>next</c> page by page.</summary>
    private async Task<List<JsonElement>> OpenCasesAsync(string host, string token, Guid? after, CancellationToken ct)
    {
        var cases = new List<JsonElement>();
        while (true)
        {
            var page = await GetAsync(host, token, "/api/hrm/duplicates?limit=200" + (after is { } a ? "&after=" + a : string.Empty), ct);
            cases.AddRange(page.GetProperty("items").EnumerateArray());
            if (!page.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
            {
                return cases;
            }

            after = next.GetGuid();
        }
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

        if (body is not null && pathAndQuery.Contains("/attendance/devices/", StringComparison.Ordinal) && pathAndQuery.Contains("/push", StringComparison.Ordinal)
            && pushSecrets.TryGetValue(Guid.Parse(pathAndQuery.Split('/')[5]), out var secret))
        {
            request.Headers.Add("X-Erp-Signature", Erp.Hrm.Application.Attendance.DeviceRules.SignatureHeader(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), body));
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
