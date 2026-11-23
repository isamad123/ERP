#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Employments;

/// <summary>
/// The employment services of slice S1.20b (Build items 5 to 10) through the real API pipeline, the real access engine, the real
/// workflow engine and job worker and a real PostgreSQL: hire (FR-HRM-EMP-02, -04, -07, FR-KRN-ENT-05, R-111), changes as events
/// with corrections (FR-HRM-EMP-03, BR-02, FR-XM-05), approval of configured kinds (FR-XM-32, ruling Q11), as-of reads and the
/// timeline (GHR-03, FR-HRM-EMP-11), the backdated change check (GHR-04, CR-096), future events by the daily job (ruling Q12), the
/// user link (FR-HRM-EMP-17), data scopes and field security on every write, and the database rules behind them.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EmploymentServiceTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The business date the services use (org.time_zone, default Asia/Karachi).</summary>
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync()
    {
        _h.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private async Task<Guid> PersonAsync() => (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;

    /// <summary>A company of its own for the test, with a place to hire into.</summary>
    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        return (company, await _h.PlaceAsync(company, Ct));
    }

    private async Task<(Guid Id, long Version, string EmployeeNo)> HireAsync(Guid user, Guid company, (Guid OrgUnit, Guid Branch) place, string? joining = null, string title = "Officer", Guid? manager = null)
    {
        var answer = await _h.PostAsync(user, Employments, EmploymentTestSupport.HireBody(company, place, await PersonAsync(), joining: joining ?? Iso(Today.AddDays(-400)), title: title, manager: manager), Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), answer.Json.GetProperty("rowVersion").GetInt64(), answer.Text("employeeNo")!);
    }

    private Task<long> VersionAsync(Guid employment) => _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment);

    private async Task<Answer> ChangeAsync(Guid user, Guid employment, object body) => await _h.PostAsync(user, $"{Employments}/{employment}/events", body, Ct);

    private async Task<string?> TitleOnAsync(Guid user, Guid employment, DateOnly date)
    {
        var answer = await _h.GetAsync(user, $"{Employments}/{employment}/as-of?date={Iso(date)}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json.GetProperty("assignment").ValueKind == JsonValueKind.Null ? null : answer.Json.GetProperty("assignment").GetProperty("title").GetString();
    }

    /// <summary>The event kinds needing approval in one company (the setting itself waits for a second administrator, FR-XM-32).</summary>
    private async Task ApprovalKindsAsync(Guid company, params string[] kinds)
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = kinds, reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(admin, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct)).Status);
        var approved = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    private Task<long> OutboxAsync(string type, Guid employment) =>
        _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = $1 and aggregate_id = $2", Ct, type, employment);

    // ------------------------------------------------------------------ hire

    [Fact]
    public async Task FR_HRM_EMP_02_FR_HRM_EMP_04_FR_HRM_EMP_07_GHR_09_a_hire_numbers_the_employment_opens_its_first_assignment_and_raises_ids_only()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var joining = Today.AddDays(-30);
        var (id, version, employeeNo) = await HireAsync(hr, company, place, Iso(joining));
        Assert.Equal(1, version);
        Assert.Matches(@"^EMP-\d{5}$", employeeNo); // kernel numbering, the company's series

        Assert.Equal("active", await _h.ScalarAsync<string>("select status from hrm.employments where id = $1", Ct, id));
        Assert.Equal(Iso(joining), Iso(await _h.ScalarAsync<DateOnly>("select service_start_date from hrm.employments where id = $1", Ct, id)));
        Assert.Equal("effective", await _h.ScalarAsync<string>("select status from hrm.employment_events where employment_id = $1 and kind = 'hire'", Ct, id));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.employment_assignments where employment_id = $1 and valid_from = $2 and valid_to is null", Ct, id, joining));

        // The whole cost goes to the org unit by default; FTE 1; not acting.
        var row = await _h.GetAsync(hr, $"/api/hrm/employment-assignments?employmentId={id}", Ct);
        var assignment = row.Json.GetProperty("items")[0];
        Assert.Equal(place.OrgUnit.ToString(), assignment.GetProperty("costAllocation")[0].GetProperty("department").GetString());
        Assert.Equal(1m, assignment.GetProperty("costAllocation")[0].GetProperty("pct").GetDecimal());
        Assert.Equal(1m, assignment.GetProperty("fte").GetDecimal());

        // hrm.employee_hired carries ids, codes and dates only; the change log names the acting HR user (GHR-09).
        var payload = await _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = 'hrm.employee_hired' and aggregate_id = $1", Ct, id);
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(["companyId", "employmentId", "eventId", "joiningDate", "personId", "status"], doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(3L, await _h.ScalarAsync<long>("""
            select count(*) from aud.change_log where actor_user_id = $1 and action = 'create'
              and entity_id in (select id from hrm.employments where id = $2 union all select id from hrm.employment_events where employment_id = $2
                                union all select id from hrm.employment_assignments where employment_id = $2)
            """, Ct, hr, id));
    }

    [Fact]
    public async Task FR_HRM_EMP_04_a_typed_employee_number_needs_the_override_and_is_never_reused()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var typed = "MAN-" + Guid.NewGuid().ToString("N")[..6];
        var first = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, place, await PersonAsync(), employeeNo: typed), Ct);
        Assert.True(first.Status == HttpStatusCode.Created, first.Body);
        Assert.Equal(typed, first.Text("employeeNo"));
        var again = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, place, await PersonAsync(), employeeNo: typed.ToLowerInvariant()), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);
        Assert.Equal("errors.hrm.employment.employee_no_taken", again.Json.GetProperty("errors").GetProperty("employeeNo")[0].GetString());

        // A number the series could still produce is refused (S1.08 ruling Q9).
        var inSeries = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, place, await PersonAsync(), employeeNo: "EMP-99999"), Ct);
        Assert.Equal("errors.numbering.manual_in_series", inSeries.Json.GetProperty("errors").GetProperty("employeeNo")[0].GetString());
    }

    [Fact]
    public async Task FR_HRM_EMP_07_R_111_a_company_limited_role_hires_a_new_person_into_its_company_in_one_transaction_and_nowhere_else()
    {
        var (mine, minePlace) = await CompanyAsync();
        var (other, otherPlace) = await CompanyAsync();
        var hr = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employments.create", "hrm.employment_assignments.read", "hrm.persons.read", "hrm.persons.create"],
            [("hrm.employments", "all"), ("hrm.employment_assignments", "all"), ("hrm.persons", "all")]);
        await _h.AssignAsync(hr, role, mine, _h.Today.AddDays(-1), null, Ct);
        var name = "Hired" + Guid.NewGuid().ToString("N")[..8];

        var hired = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(mine, minePlace, newPerson: new { firstName = name, gender = "female" }), Ct);
        Assert.True(hired.Status == HttpStatusCode.Created, hired.Body);
        var person = hired.Json.GetProperty("person").GetProperty("id").GetGuid();
        Assert.Equal(person, hired.Json.GetProperty("employment").GetProperty("personId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(hr, $"/api/hrm/persons/{person}", Ct)).Status); // visible through the employment

        // Into another company: refused, and neither the person nor an employment is left behind.
        var refused = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(other, otherPlace, newPerson: new { firstName = name + "x", gender = "male" }), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.persons where first_name = $1", Ct, name + "x"));

        // A refusal inside the transaction (the person is new, the manager is not current) rolls the person back too.
        var bad = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(mine, minePlace, newPerson: new { firstName = name + "y", gender = "male" }, manager: Guid.NewGuid()), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.persons where first_name = $1", Ct, name + "y"));
    }

    [Fact]
    public async Task FR_HRM_EMP_02_a_hire_is_validated_and_the_database_keeps_cost_shares_and_references_inside_the_company()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (other, otherPlace) = await CompanyAsync();
        var missing = await _h.PostAsync(hr, Employments, new { companyId = company, personId = await PersonAsync(), employeeType = "boss", assignment = new { } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        var errors = missing.Json.GetProperty("errors");
        Assert.Equal("errors.hrm.field.unknown_code", errors.GetProperty("employeeType")[0].GetString());
        Assert.Equal("errors.hrm.field.required", errors.GetProperty("joiningDate")[0].GetString());
        Assert.Equal("errors.hrm.field.required", errors.GetProperty("assignment.orgUnitId")[0].GetString());
        Assert.Equal("errors.hrm.field.required", errors.GetProperty("assignment.title")[0].GetString());

        var shares = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, place, await PersonAsync(),
            costAllocation: new object[] { new { costCenterRef = "CC-1", percent = 60 }, new { department = place.OrgUnit, percent = 30 } }), Ct);
        Assert.Equal("errors.hrm.assignment.cost_sum", shares.Json.GetProperty("errors").GetProperty("assignment.costAllocation")[0].GetString());

        // A department or org unit of another company is refused by the database (composite key, cost allocation rule).
        var foreignDept = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, place, await PersonAsync(),
            costAllocation: new object[] { new { department = otherPlace.OrgUnit, percent = 100 } }), Ct);
        Assert.Equal("errors.hrm.assignment.not_in_company", foreignDept.Json.GetProperty("errors").GetProperty("assignment.costAllocation")[0].GetString());
        var foreignUnit = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, (otherPlace.OrgUnit, place.Branch), await PersonAsync(),
            costAllocation: new object[] { new { costCenterRef = "CC-1", percent = 100 } }), Ct);
        Assert.Equal("errors.hrm.assignment.not_in_company", foreignUnit.Json.GetProperty("errors").GetProperty("assignment.orgUnitId")[0].GetString());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employments where company_id = $1", Ct, company));

        // One current main employment per person (FR-XM-28).
        var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, place, person), Ct)).Status);
        var twice = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(other, otherPlace, person), Ct);
        Assert.Equal(HttpStatusCode.Conflict, twice.Status);
        Assert.Equal("errors.hrm.employment.already_employed", twice.Text("title"));
    }

    [Fact]
    public async Task FR_KRN_ENT_05_hiring_beyond_the_active_employee_limit_is_refused_with_the_licence_message_and_the_meter_counts_active_employees()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        // Seats count persons, not employments (S1.20c, contracts register "HR active employee counters count persons"): a person in
        // the middle of a transfer between companies has two current employments and takes one seat. Counting rows here would leave
        // room for a second hire whenever another test has left a transfer in this shared customer.
        var seats = await _h.ScalarAsync<long>("select count(distinct person_id) from hrm.employments where status in ('pre_joining', 'active', 'on_notice')", Ct);
        var claims = await fx.SetClaimAsync(_h.Code, "limits,employees.active", seats + 1, Ct);
        try
        {
            await HireAsync(hr, company, place, Iso(Today.AddDays(10))); // a future joiner takes a seat too
            var refused = await _h.PostAsync(hr, Employments, EmploymentTestSupport.HireBody(company, place, await PersonAsync()), Ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Equal("errors.licence.limit_reached", refused.Text("title"));
            Assert.Contains("employees.active", refused.Body, StringComparison.Ordinal);
        }
        finally
        {
            await fx.RestoreClaimsAsync(_h.Code, claims, Ct);
        }

        // The monthly meter (S1.09b ruling Q8) counts persons with an active or on-notice employment, not future joiners (S1.20c).
        await using var c = await _h.OwnerAsync(null, Ct);
        var meter = await new Erp.Hrm.Infrastructure.Employments.ActiveEmployeeCounter().CountAsync(c, Ct);
        Assert.Equal(await _h.ScalarAsync<long>("select count(distinct person_id) from hrm.employments where status in ('active', 'on_notice')", Ct), meter);
    }

    // ------------------------------------------------------------------ changes, corrections, as-of, timeline (EMP AC)

    [Fact]
    public async Task FR_HRM_EMP_03_FR_HRM_EMP_11_GHR_03_GHR_04_FR_XM_05_EMP_AC_promote_with_a_future_date_correct_it_backward_and_read_as_of_and_the_timeline()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        await ApprovalKindsAsync(company); // approval off here: the changes apply at once
        var (id, _, _) = await HireAsync(hr, company, place, Iso(Today.AddDays(-200)));

        // Promote with a future date: approved, not yet effective; the as-of read shows the old title until the date.
        var future = Today.AddDays(30);
        var promoted = await ChangeAsync(hr, id, new { kind = "promotion", reasonCode = "merit", effectiveDate = Iso(future), assignment = new { title = "Senior officer" }, rowVersion = await VersionAsync(id) });
        Assert.True(promoted.Status == HttpStatusCode.Created, promoted.Body);
        Assert.Equal("approved", promoted.Text("status"));
        Assert.Equal("hrm.impact.not_backdated", promoted.Json.GetProperty("impact").GetProperty("summary").GetString());
        var promotion = promoted.Json.GetProperty("eventId").GetGuid();
        Assert.Equal("Officer", await TitleOnAsync(hr, id, future.AddDays(-1)));
        Assert.Equal("Senior officer", await TitleOnAsync(hr, id, future));
        Assert.Equal(0L, await OutboxAsync("hrm.employee_changed", id)); // raised on the date (ruling Q12)
        var promotionRow = await _h.ScalarAsync<string>("select row_to_json(e)::text from (select kind, reason_code, effective_date, changes, created_at, created_by from hrm.employment_events where id = $1) e", Ct, promotion);

        // Correct the promotion date backward: a new data_correction event; the check runs (dated in the past) and reports truthfully.
        var corrected = Today.AddDays(-20);
        var check = await _h.PostAsync(hr, $"{Employments}/{id}/events/check", new { kind = "data_correction", reasonCode = "entry_error", effectiveDate = Iso(corrected), correctsEventId = promotion }, Ct);
        Assert.True(check.Status == HttpStatusCode.OK, check.Body);
        Assert.True(check.Json.GetProperty("impact").GetProperty("backdated").GetBoolean());
        Assert.Equal("hrm.impact.nothing_affected", check.Json.GetProperty("impact").GetProperty("summary").GetString());
        var correction = await ChangeAsync(hr, id, new { kind = "data_correction", reasonCode = "entry_error", effectiveDate = Iso(corrected), correctsEventId = promotion, rowVersion = await VersionAsync(id) });
        Assert.True(correction.Status == HttpStatusCode.Created, correction.Body);
        Assert.Equal("effective", correction.Text("status"));
        var correctionId = correction.Json.GetProperty("eventId").GetGuid();

        Assert.Equal("Officer", await TitleOnAsync(hr, id, corrected.AddDays(-1)));
        Assert.Equal("Senior officer", await TitleOnAsync(hr, id, corrected));
        Assert.Equal("Senior officer", await TitleOnAsync(hr, id, future));

        // Nothing is edited in place: the promotion's content is as recorded; only its status moved on (it never takes effect).
        Assert.Equal(promotionRow, await _h.ScalarAsync<string>("select row_to_json(e)::text from (select kind, reason_code, effective_date, changes, created_at, created_by from hrm.employment_events where id = $1) e", Ct, promotion));
        Assert.Equal("cancelled", await _h.ScalarAsync<string>("select status from hrm.employment_events where id = $1", Ct, promotion));

        // The timeline lists hire, promotion and correction with who and when, newest date first, filterable by kind.
        var timeline = await _h.GetAsync(hr, $"{Employments}/{id}/timeline", Ct);
        Assert.True(timeline.Status == HttpStatusCode.OK, timeline.Body);
        var items = timeline.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["promotion", "data_correction", "hire"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.All(items, i => Assert.Equal(hr, i.GetProperty("createdBy").GetGuid()));
        Assert.Equal(correctionId, items[0].GetProperty("replacedBy").GetGuid());
        Assert.Equal("Senior officer", items[1].GetProperty("changes").GetProperty("assignment").GetProperty("title").GetString());
        Assert.Single((await _h.GetAsync(hr, $"{Employments}/{id}/timeline?kind=hire", Ct)).Json.GetProperty("items").EnumerateArray());

        // The generated list answers as of a date too (GHR-03), and the event raised once with the earliest affected day.
        var asOf = await _h.GetAsync(hr, $"/api/hrm/employment-assignments?employmentId={id}&asOf={Iso(corrected.AddDays(-1))}", Ct);
        Assert.Equal("Officer", Assert.Single(asOf.Json.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        var changed = await _h.ScalarAsync<string>("select payload->>'earliestDate' from hrm.outbox_messages where event_type = 'hrm.employee_changed' and aggregate_id = $1", Ct, id);
        Assert.Equal(Iso(corrected), changed);

        // A second correction of the same event is refused: correct the correction instead.
        var twice = await ChangeAsync(hr, id, new { kind = "data_correction", reasonCode = "entry_error", effectiveDate = Iso(corrected.AddDays(1)), correctsEventId = promotion, rowVersion = await VersionAsync(id) });
        Assert.Equal("errors.hrm.employment_event.already_corrected", twice.Json.GetProperty("errors").GetProperty("correctsEventId")[0].GetString());
    }

    [Fact]
    public async Task FR_HRM_EMP_03_BR_02_changes_are_checked_by_kind_and_a_backdated_change_between_two_rows_carries_forward()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        await ApprovalKindsAsync(company);
        var joining = Today.AddDays(-300);
        var (id, _, _) = await HireAsync(hr, company, place, Iso(joining));
        Assert.Equal(HttpStatusCode.Created, (await ChangeAsync(hr, id, new { kind = "promotion", reasonCode = "merit", effectiveDate = Iso(Today.AddDays(-100)), assignment = new { title = "Lead" }, rowVersion = await VersionAsync(id) })).Status);

        // A grade change dated between the two rows splits the first and carries into the second (it sets another field).
        var unit = (Guid)(await _h.RowAsync("org.org_units", company, null, Ct))["id"]!;
        var transfer = await ChangeAsync(hr, id, new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today.AddDays(-200)), assignment = new { orgUnitId = unit }, rowVersion = await VersionAsync(id) });
        Assert.True(transfer.Status == HttpStatusCode.Created, transfer.Body);
        var rows = await _h.GetAsync(hr, $"/api/hrm/employment-assignments?employmentId={id}&sort=validFrom", Ct);
        var list = rows.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([Iso(joining), Iso(Today.AddDays(-200)), Iso(Today.AddDays(-100))], list.Select(r => r.GetProperty("validFrom").GetString()));
        Assert.Equal([place.OrgUnit, unit, unit], list.Select(r => r.GetProperty("orgUnitId").GetGuid()));
        Assert.Equal(["Officer", "Officer", "Lead"], list.Select(r => r.GetProperty("title").GetString()));

        // Kinds: a promotion needs job changes; a confirmation none and not before joining (BR-02); dates not before joining.
        var version = await VersionAsync(id);
        var empty = await ChangeAsync(hr, id, new { kind = "promotion", reasonCode = "merit", effectiveDate = Iso(Today), rowVersion = version });
        Assert.Equal("errors.hrm.employment_event.no_assignment_changes", empty.Json.GetProperty("errors").GetProperty("assignment")[0].GetString());
        var early = await ChangeAsync(hr, id, new { kind = "confirmation", reasonCode = "probation_completed", effectiveDate = Iso(joining.AddDays(-1)), rowVersion = version });
        Assert.Equal("errors.hrm.employment.confirmation_before_joining", early.Json.GetProperty("errors").GetProperty("effectiveDate")[0].GetString());
        var wrongReason = await ChangeAsync(hr, id, new { kind = "promotion", reasonCode = "new_hire", effectiveDate = Iso(Today), assignment = new { title = "X" }, rowVersion = version });
        Assert.Equal("errors.hrm.field.unknown_code", wrongReason.Json.GetProperty("errors").GetProperty("reasonCode")[0].GetString());
        var hire = await ChangeAsync(hr, id, new { kind = "hire", reasonCode = "new_hire", effectiveDate = Iso(Today), rowVersion = version });
        Assert.Equal("errors.hrm.employment_event.kind_not_recorded", hire.Json.GetProperty("errors").GetProperty("kind")[0].GetString());
        var fields = await ChangeAsync(hr, id, new { kind = "promotion", reasonCode = "merit", effectiveDate = Iso(Today), assignment = new { title = "Y" }, employment = new { employeeType = "contract" }, rowVersion = version });
        Assert.Equal("errors.hrm.employment_event.employment_fields_by_correction", fields.Json.GetProperty("errors").GetProperty("employment")[0].GetString());
        var before = await ChangeAsync(hr, id, new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(joining.AddDays(-1)), assignment = new { title = "Z" }, rowVersion = version });
        Assert.Equal("errors.hrm.employment.before_joining", before.Json.GetProperty("errors").GetProperty("effectiveDate")[0].GetString());
        var stale = await ChangeAsync(hr, id, new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today), assignment = new { title = "Z" }, rowVersion = version - 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);

        // A confirmation sets the confirmation date; employment fields change only through a correction (ruling Q2).
        Assert.Equal(HttpStatusCode.Created, (await ChangeAsync(hr, id, new { kind = "confirmation", reasonCode = "probation_completed", effectiveDate = Iso(joining.AddDays(90)), rowVersion = version })).Status);
        Assert.Equal(Iso(joining.AddDays(90)), Iso(await _h.ScalarAsync<DateOnly>("select confirmation_date from hrm.employments where id = $1", Ct, id)));
        var corrected = await ChangeAsync(hr, id, new { kind = "data_correction", reasonCode = "late_information", effectiveDate = Iso(Today), employment = new { employeeType = "contract", expectedEndDate = Iso(Today.AddDays(365)) }, rowVersion = await VersionAsync(id) });
        Assert.True(corrected.Status == HttpStatusCode.Created, corrected.Body);
        Assert.Equal("contract", await _h.ScalarAsync<string>("select employee_type from hrm.employments where id = $1", Ct, id));

        // Correcting the hire's date moves the joining date and the first row (the service start date followed it).
        var hireEvent = await _h.ScalarAsync<Guid>("select id from hrm.employment_events where employment_id = $1 and kind = 'hire'", Ct, id);
        var moved = await ChangeAsync(hr, id, new { kind = "data_correction", reasonCode = "entry_error", effectiveDate = Iso(joining.AddDays(-10)), correctsEventId = hireEvent, rowVersion = await VersionAsync(id) });
        Assert.True(moved.Status == HttpStatusCode.Created, moved.Body);
        Assert.Equal(Iso(joining.AddDays(-10)), Iso(await _h.ScalarAsync<DateOnly>("select joining_date from hrm.employments where id = $1", Ct, id)));
        Assert.Equal(Iso(joining.AddDays(-10)), Iso(await _h.ScalarAsync<DateOnly>("select service_start_date from hrm.employments where id = $1", Ct, id)));
        Assert.Equal("Officer", await TitleOnAsync(hr, id, joining.AddDays(-10)));
    }

    [Fact]
    public async Task FR_XM_32_Q11_a_promotion_of_a_kind_set_to_need_approval_waits_the_requester_cannot_approve_and_it_applies_on_approval()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (id, _, _) = await HireAsync(hr, company, place, Iso(Today.AddDays(-100)));

        // Default setting: promotions need approval (ruling Q11).
        var asked = await ChangeAsync(hr, id, new { kind = "promotion", reasonCode = "merit", effectiveDate = Iso(Today), assignment = new { title = "Manager" }, rowVersion = await VersionAsync(id) });
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        Assert.Equal("pending_approval", asked.Text("status"));
        var instance = asked.Json.GetProperty("instanceId").GetGuid();
        var eventId = asked.Json.GetProperty("eventId").GetGuid();
        Assert.Equal("Officer", await TitleOnAsync(hr, id, Today));

        // The requester has no task and cannot decide the one there is; another HR administrator approves.
        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, instance);
        var own = await _h.PostAsync(hr, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct);
        Assert.NotEqual(HttpStatusCode.OK, own.Status);
        Assert.Equal("pending_approval", await _h.ScalarAsync<string>("select status from hrm.employment_events where id = $1", Ct, eventId));
        var approved = await _h.PostAsync(checker, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("effective", await _h.ScalarAsync<string>("select status from hrm.employment_events where id = $1", Ct, eventId));
        Assert.Equal("Manager", await TitleOnAsync(hr, id, Today));
        Assert.Equal(1L, await OutboxAsync("hrm.employee_changed", id));

        // Review S1.20b finding 1: job details cannot change through an untargeted correction, and a correction of a promotion
        // waits for the route like the promotion itself (FR-XM-32).
        var untargeted = await ChangeAsync(hr, id, new { kind = "data_correction", reasonCode = "entry_error", effectiveDate = Iso(Today), assignment = new { title = "Director" }, rowVersion = await VersionAsync(id) });
        Assert.Equal("errors.hrm.employment_event.job_details_need_target", untargeted.Json.GetProperty("errors").GetProperty("assignment")[0].GetString());
        var correction = await ChangeAsync(hr, id, new { kind = "data_correction", reasonCode = "entry_error", effectiveDate = Iso(Today), correctsEventId = eventId, assignment = new { title = "Director" }, rowVersion = await VersionAsync(id) });
        Assert.True(correction.Status == HttpStatusCode.Accepted, correction.Body);
        Assert.Equal("Manager", await TitleOnAsync(hr, id, Today));
        var correctionTask = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, correction.Json.GetProperty("instanceId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/wf/tasks/{correctionTask}/reject", new { comment = "no" }, Ct)).Status);

        // A rejected one is cancelled and changes nothing.
        var second = await ChangeAsync(hr, id, new { kind = "demotion", reasonCode = "performance", effectiveDate = Iso(Today), assignment = new { title = "Clerk" }, rowVersion = await VersionAsync(id) });
        var secondTask = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, second.Json.GetProperty("instanceId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/wf/tasks/{secondTask}/reject", new { comment = "no" }, Ct)).Status);
        Assert.Equal("cancelled", await _h.ScalarAsync<string>("select status from hrm.employment_events where id = $1", Ct, second.Json.GetProperty("eventId").GetGuid()));
        Assert.Equal("Manager", await TitleOnAsync(hr, id, Today));
    }

    [Fact]
    public async Task Q12_a_future_hire_is_pre_joining_until_the_daily_job_makes_it_active_and_announces_it_once()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (id, _, _) = await HireAsync(hr, company, place, Iso(Today.AddDays(5)));
        Assert.Equal("pre_joining", await _h.ScalarAsync<string>("select status from hrm.employments where id = $1", Ct, id));
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.employment_events where employment_id = $1", Ct, id));
        Assert.Null(await TitleOnAsync(hr, id, Today)); // no job details before the joining date

        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        _h.Clock.Advance(TimeSpan.FromDays(6));
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.employment_events_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("active", await _h.ScalarAsync<string>("select status from hrm.employments where id = $1", Ct, id));
        Assert.Equal("effective", await _h.ScalarAsync<string>("select status from hrm.employment_events where employment_id = $1", Ct, id));
        Assert.Equal(1L, await OutboxAsync("hrm.employee_changed", id));

        await rig.QueueAsync("hrm.employment_events_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal(1L, await OutboxAsync("hrm.employee_changed", id)); // once
        var log = await _h.ScalarAsync<string>("select string_agg(actor_kind || ':' || action || ':' || entity_type, ',' order by occurred_at) from aud.change_log where entity_id in ($1, (select id from hrm.employment_events where employment_id = $1))", Ct, id);
        Assert.True(log.Contains("job:status:hrm.employments", StringComparison.Ordinal) && log.Contains("job:status:hrm.employment_events", StringComparison.Ordinal), log); // under the job actor
    }

    [Fact]
    public async Task GHR_04_CR_096_a_backdated_change_inside_a_locked_period_of_a_registered_module_is_flagged_never_refused()
    {
        var module = new LockedPayroll();
        await using var app = await fx.StartAppAsync(null, Ct, services =>
        {
            services.Remove(services.First(d => d.ServiceType == typeof(Erp.BuildingBlocks.Domain.Time.IClock)));
            services.AddSingleton<Erp.BuildingBlocks.Domain.Time.IClock>(_h.Clock);
            services.AddSingleton<Erp.Hrm.Contracts.Employments.IEmploymentChangeImpact>(module);
        }, realAccess: true);
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        await ApprovalKindsAsync(company);
        var (id, _, _) = await HireAsync(hr, company, place, Iso(Today.AddDays(-120)));
        module.Locked = (Today.AddDays(-60), Today.AddDays(-31));

        using var client = TenancyFixture.Client(app, TenancyFixture.HostOf(_h.Code), _h.Token(hr));
        var body = new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today.AddDays(-45)), assignment = new { title = "Moved" }, rowVersion = await VersionAsync(id) };
        using var response = await client.PostAsJsonAsync($"{Employments}/{id}/events", body, JsonSerializerOptions.Web, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var answer = JsonDocument.Parse(text);
        var impact = answer.RootElement.GetProperty("impact");
        Assert.Equal("hrm.impact.periods_flagged", impact.GetProperty("summary").GetString());
        Assert.True(impact.GetProperty("flagged")[0].GetProperty("locked").GetBoolean());
        Assert.False(impact.GetProperty("terminationNeedsOverride").GetBoolean());
        Assert.Equal(1, module.Calls);

        // The flags are kept on the event, and the change took effect (a retroactive adjustment in the next open payroll, FR-XM-26).
        Assert.Equal("hrm.impact.periods_flagged", await _h.ScalarAsync<string>("select changes->'impact'->>'summary' from hrm.employment_events where id = $1", Ct, answer.RootElement.GetProperty("eventId").GetGuid()));
        Assert.Equal("Moved", await TitleOnAsync(hr, id, Today.AddDays(-45)));
    }

    private sealed class LockedPayroll : Erp.Hrm.Contracts.Employments.IEmploymentChangeImpact
    {
        public (DateOnly From, DateOnly To) Locked { get; set; }

        public int Calls { get; private set; }

        public string ModuleCode => "test.payroll";

        public Task<Erp.Hrm.Contracts.Employments.EmploymentImpactAnswer> AnalyseAsync(Erp.Hrm.Contracts.Employments.EmploymentChange change, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Erp.Hrm.Contracts.Employments.EmploymentImpactAnswer(ModuleCode, [new("payroll", Locked.From, Locked.To, true)], null, []));
        }
    }

    // ------------------------------------------------------------------ user link, kernel contracts

    [Fact]
    public async Task FR_HRM_EMP_17_Q8_an_employment_links_to_one_user_per_company_unlinking_keeps_the_history_and_the_employee_sees_their_own_record()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (id, version, _) = await HireAsync(hr, company, place);
        var (other, otherVersion, _) = await HireAsync(hr, company, place);
        var employee = await _h.UserAsync(Ct, null, "employee");

        var linked = await _h.PutAsync(hr, $"{Employments}/{id}/user", new { userId = employee, rowVersion = version }, Ct);
        Assert.True(linked.Status == HttpStatusCode.OK, linked.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(employee, $"{Employments}/{id}/as-of", Ct)).Status); // own records
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(employee, $"{Employments}/{other}/as-of", Ct)).Status);

        // One human per user: the same user on another person's employment is refused; an unknown user too.
        var twice = await _h.PutAsync(hr, $"{Employments}/{other}/user", new { userId = employee, rowVersion = otherVersion }, Ct);
        Assert.Equal("errors.hrm.user_link.other_person", twice.Text("title"));
        var unknown = await _h.PutAsync(hr, $"{Employments}/{other}/user", new { userId = Guid.NewGuid(), rowVersion = otherVersion }, Ct);
        Assert.Equal("errors.hrm.user_link.user_not_found", unknown.Json.GetProperty("errors").GetProperty("userId")[0].GetString());

        // Unlinking keeps the history in the change log; the employee no longer sees the record.
        var unlinked = await _h.DeleteAsync(hr, $"{Employments}/{id}/user?rowVersion={linked.Json.GetProperty("rowVersion").GetInt64()}", Ct);
        Assert.True(unlinked.Status == HttpStatusCode.OK, unlinked.Body);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(employee, $"{Employments}/{id}/as-of", Ct)).Status);
        Assert.Equal(employee.ToString(), await _h.ScalarAsync<string>("""
            select c->>'before' from aud.change_log l, jsonb_array_elements(l.changes) c where l.entity_id = $1 and l.action = 'update' and c->>'field' = 'user_ref' and c->>'after' is null
            """, Ct, id));

        // Nobody links their own user (review S1.20b finding 5).
        var self = await _h.PutAsync(hr, $"{Employments}/{other}/user", new { userId = hr, rowVersion = otherVersion }, Ct);
        Assert.Equal("errors.hrm.user_link.own_user", self.Text("title"));

        // Linking needs its own (sensitive) permission.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(employee, $"{Employments}/{other}/user", new { userId = employee, rowVersion = otherVersion }, Ct)).Status);
    }

    [Fact]
    public async Task S1_10_Q4_a_line_manager_route_resolves_the_employees_manager_on_the_date_the_managers_manager_and_no_manager_resolves_nobody()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (top, topVersion, _) = await HireAsync(hr, company, place, title: "Director");
        var (mid, midVersion, _) = await HireAsync(hr, company, place, title: "Manager", manager: top);
        var (staff, staffVersion, _) = await HireAsync(hr, company, place, title: "Officer", manager: mid);
        var (topUser, midUser, staffUser) = (await _h.UserAsync(Ct), await _h.UserAsync(Ct), await _h.UserAsync(Ct));
        foreach (var (e, v, u) in new[] { (top, topVersion, topUser), (mid, midVersion, midUser), (staff, staffVersion, staffUser) })
        {
            Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(hr, $"{Employments}/{e}/user", new { userId = u, rowVersion = v }, Ct)).Status);
        }

        var builder = new NpgsqlConnectionStringBuilder(fx.AdminConnectionString) { Database = Erp.Kernel.Infrastructure.ControlPlane.TenantLogins.DatabaseName(_h.Code), Pooling = false };
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        var directory = new Erp.Hrm.Infrastructure.Employments.HrmApproverDirectory(new BoundTenant(source));
        Assert.Equal([midUser], await directory.ManagersAsync(staffUser, 1, Today, Ct));
        Assert.Equal([topUser], await directory.ManagersAsync(staffUser, 2, Today, Ct));
        Assert.Empty(await directory.ManagersAsync(topUser, 1, Today, Ct)); // no manager: the engine's "cannot resolve"
        Assert.Empty(await directory.ManagersAsync(staffUser, 1, Today.AddDays(-500), Ct)); // before the employment
        Assert.True(directory.Supports("manager"));
        Assert.False(directory.Supports("department_head")); // S1.22 (ruling Q7)

        // A manager change dated in the past moves the answer on that date only; a reporting circle is refused.
        await ApprovalKindsAsync(company);
        var circle = await ChangeAsync(hr, top, new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today), assignment = new { managerEmploymentId = staff }, rowVersion = await VersionAsync(top) });
        Assert.Equal("errors.hrm.assignment.manager_cycle", circle.Json.GetProperty("errors").GetProperty("assignment.managerEmploymentId")[0].GetString());
        var self = await ChangeAsync(hr, top, new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today), assignment = new { managerEmploymentId = top }, rowVersion = await VersionAsync(top) });
        Assert.Equal("errors.hrm.assignment.own_manager", self.Json.GetProperty("errors").GetProperty("assignment.managerEmploymentId")[0].GetString());
    }

    /// <summary>The customer database for a directory built outside a request (the API binds it from the host name).</summary>
    private sealed class BoundTenant(NpgsqlDataSource source) : Erp.BuildingBlocks.Application.Tenancy.ITenantConnectionProvider
    {
        public System.Data.Common.DbDataSource DataSource => source;
    }

    // ------------------------------------------------------------------ access: permissions, data scopes, field security

    [Fact]
    public async Task FR_KRN_IAM_12_FR_HRM_ROL_02_writes_and_reads_follow_the_permission_and_the_company_team_and_own_scopes()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (otherCompany, otherPlace) = await CompanyAsync();
        await ApprovalKindsAsync(company);
        await ApprovalKindsAsync(otherCompany);
        var managerUser = await _h.UserAsync(Ct, null, "line_manager");
        var (manager, managerVersion, _) = await HireAsync(hr, company, place, title: "Manager");
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(hr, $"{Employments}/{manager}/user", new { userId = managerUser, rowVersion = managerVersion }, Ct)).Status);
        var (report, _, _) = await HireAsync(hr, company, place, manager: manager);
        var (stranger, _, _) = await HireAsync(hr, company, place);
        var (elsewhere, _, _) = await HireAsync(hr, otherCompany, otherPlace);

        // Team: the line manager reads the direct report as of a date, nobody else's; holds no event permission.
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(managerUser, $"{Employments}/{report}/as-of", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(managerUser, $"{Employments}/{stranger}/as-of", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChangeAsync(managerUser, report, new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(Today), assignment = new { title = "Acting" }, rowVersion = await VersionAsync(report) })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(managerUser, $"{Employments}/{report}/timeline", Ct)).Status);

        // Company: a role limited to one company changes its employments only; another company's is not found.
        var limited = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_assignments.read", "hrm.employment_events.read", "hrm.employment_events.create"],
            [("hrm.employments", "all"), ("hrm.employment_assignments", "all"), ("hrm.employment_events", "all")]);
        await _h.AssignAsync(limited, role, company, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Created, (await ChangeAsync(limited, stranger, new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(Today), assignment = new { title = "Acting" }, rowVersion = await VersionAsync(stranger) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChangeAsync(limited, elsewhere, new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(Today), assignment = new { title = "Acting" }, rowVersion = await VersionAsync(elsewhere) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(limited, $"{Employments}/{elsewhere}/timeline", Ct)).Status);

        // Branch scope after the change: a role limited to one branch cannot move someone into another branch.
        var branch2 = (Guid)(await _h.RowAsync("org.branches", company, null, Ct))["id"]!;
        var branchUser = await _h.UserAsync(Ct);
        var branchRole = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_assignments.read", "hrm.employment_events.create"],
            [("hrm.employments", "all"), ("hrm.employment_assignments", await _h.ScopeAsync("branches", new { branch_ids = new[] { place.Branch } }, Ct)), ("hrm.employment_events", "all")]);
        await _h.AssignAsync(branchUser, branchRole, null, _h.Today.AddDays(-1), null, Ct);
        var moved = await ChangeAsync(branchUser, stranger, new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today.AddDays(1)), assignment = new { branchId = branch2 }, rowVersion = await VersionAsync(stranger) });
        Assert.Equal(HttpStatusCode.Forbidden, moved.Status);

        // Generated writes of the three tables are closed (ruling Q1, R-113).
        foreach (var path in new[] { "/api/hrm/employment-assignments", "/api/hrm/employment-events" })
        {
            Assert.Contains((await _h.PostAsync(hr, path, new { employmentId = report }, Ct)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }

    [Fact]
    public async Task FR_KRN_IAM_13_a_field_hidden_by_the_role_is_left_out_of_the_as_of_read_and_cannot_be_changed()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        await ApprovalKindsAsync(company);
        var (id, _, _) = await HireAsync(hr, company, place);
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_assignments.read", "hrm.employment_events.create"],
            [("hrm.employments", "all"), ("hrm.employment_assignments", "all"), ("hrm.employment_events", "all")],
            [("hrm.employment_assignments", "title", "hidden")]);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);

        var read = await _h.GetAsync(user, $"{Employments}/{id}/as-of", Ct);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.False(read.Json.GetProperty("assignment").TryGetProperty("title", out _));
        var change = await ChangeAsync(user, id, new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(Today), assignment = new { title = "Hidden change" }, rowVersion = await VersionAsync(id) });
        Assert.Equal(HttpStatusCode.BadRequest, change.Status);
        Assert.Equal("errors.access.field_not_editable", change.Json.GetProperty("errors").GetProperty("title")[0].GetString());

        // Review S1.20b finding 4: the timeline shows the job details of each event by the same rules (title hidden, unit shown),
        // and nothing of them without the assignment read; the generated event read hides `changes` (salary class, GHR-06).
        var reader = await _h.UserAsync(Ct);
        var readRole = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_assignments.read", "hrm.employment_events.read"],
            [("hrm.employments", "all"), ("hrm.employment_assignments", "all"), ("hrm.employment_events", "all")], [("hrm.employment_assignments", "title", "hidden")]);
        await _h.AssignAsync(reader, readRole, null, _h.Today.AddDays(-1), null, Ct);
        var hire = (await _h.GetAsync(reader, $"{Employments}/{id}/timeline", Ct)).Json.GetProperty("items")[0].GetProperty("changes").GetProperty("assignment");
        Assert.False(hire.TryGetProperty("title", out _));
        Assert.Equal(place.OrgUnit.ToString(), hire.GetProperty("org_unit_id").GetString());

        var eventsOnly = await _h.UserAsync(Ct);
        var eventsRole = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_events.read"], [("hrm.employments", "all"), ("hrm.employment_events", "all")]);
        await _h.AssignAsync(eventsOnly, eventsRole, null, _h.Today.AddDays(-1), null, Ct);
        Assert.False((await _h.GetAsync(eventsOnly, $"{Employments}/{id}/timeline", Ct)).Json.GetProperty("items")[0].GetProperty("changes").TryGetProperty("assignment", out _));
        var generated = await _h.GetAsync(eventsOnly, $"/api/hrm/employment-events?employmentId={id}", Ct);
        Assert.Equal(HttpStatusCode.OK, generated.Status);
        Assert.False(generated.Json.GetProperty("items")[0].TryGetProperty("changes", out _));
    }

    [Fact]
    public async Task FR_XM_32_review_12_the_employee_the_change_is_about_cannot_approve_it_even_holding_the_approver_role()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var subject = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (id, version, _) = await HireAsync(hr, company, place);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(hr, $"{Employments}/{id}/user", new { userId = subject, rowVersion = version }, Ct)).Status);

        var asked = await ChangeAsync(hr, id, new { kind = "promotion", reasonCode = "merit", effectiveDate = Iso(Today), assignment = new { title = "Head" }, rowVersion = await VersionAsync(id) });
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, asked.Json.GetProperty("instanceId").GetGuid());
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(subject, $"/api/wf/tasks/{task}/approve", new { comment = "my own promotion" }, Ct)).Status);
        Assert.Equal("pending_approval", await _h.ScalarAsync<string>("select status from hrm.employment_events where id = $1", Ct, asked.Json.GetProperty("eventId").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct)).Status);
        Assert.Equal("Head", await TitleOnAsync(hr, id, Today));
    }

    [Fact]
    public async Task FR_KRN_IAM_12_review_3_a_backdated_change_cannot_rewrite_later_job_details_in_a_branch_outside_the_callers_records()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        await ApprovalKindsAsync(company);
        var (id, _, _) = await HireAsync(hr, company, place, Iso(Today.AddDays(-300)));
        var branchB = (Guid)(await _h.RowAsync("org.branches", company, null, Ct))["id"]!;
        Assert.Equal(HttpStatusCode.Created, (await ChangeAsync(hr, id, new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today.AddDays(-100)), assignment = new { branchId = branchB }, rowVersion = await VersionAsync(id) })).Status);

        // An officer limited to branch A records a title change dated while the employee was still in branch A: it would carry
        // into the branch-B rows from the transfer on, which the officer may not touch.
        var officer = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_assignments.read", "hrm.employment_events.create"],
            [("hrm.employments", "all"), ("hrm.employment_assignments", await _h.ScopeAsync("branches", new { branch_ids = new[] { place.Branch } }, Ct)), ("hrm.employment_events", "all")]);
        await _h.AssignAsync(officer, role, null, _h.Today.AddDays(-1), null, Ct);
        var backdated = await ChangeAsync(officer, id, new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(Today.AddDays(-200)), assignment = new { title = "Acting lead" }, rowVersion = await VersionAsync(id) });
        Assert.Equal(HttpStatusCode.Forbidden, backdated.Status);
        Assert.Equal("Officer", await TitleOnAsync(hr, id, Today));
    }

    [Fact]
    public async Task FR_KRN_IAM_12_the_subtree_scope_reaches_the_units_below_the_users_own_and_no_further()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var hierarchy = (Guid)(await _h.RowAsync("org.org_hierarchies", company, new Dictionary<string, object?> { ["kind"] = "reporting" }, Ct))["id"]!;
        var child = (Guid)(await _h.RowAsync("org.org_units", company, null, Ct))["id"]!;
        var unrelated = (Guid)(await _h.RowAsync("org.org_units", company, null, Ct))["id"]!;
        await _h.RowAsync("org.org_unit_links", company, new Dictionary<string, object?> { ["hierarchy_id"] = hierarchy, ["unit_id"] = child, ["parent_unit_id"] = place.OrgUnit, ["valid_from"] = Today.AddDays(-1000), ["valid_to"] = null }, Ct);
        var (head, headVersion, _) = await HireAsync(hr, company, place, title: "Head");
        var (below, _, _) = await HireAsync(hr, company, (child, place.Branch));
        var (outside, _, _) = await HireAsync(hr, company, (unrelated, place.Branch));
        var user = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(hr, $"{Employments}/{head}/user", new { userId = user, rowVersion = headVersion }, Ct)).Status);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_assignments.read"], [("hrm.employments", "subtree"), ("hrm.employment_assignments", "subtree")]);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user, $"{Employments}/{below}/as-of", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user, $"{Employments}/{head}/as-of", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user, $"{Employments}/{outside}/as-of", Ct)).Status);
    }

    // ------------------------------------------------------------------ database rules

    [Fact]
    public async Task FR_XM_05_the_database_refuses_editing_an_effective_event_a_reporting_circle_and_cost_shares_not_summing_to_one()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (a, _, _) = await HireAsync(hr, company, place);
        var (b, _, _) = await HireAsync(hr, company, place, manager: a);
        await using var c = await _h.OwnerAsync(null, Ct);

        var edit = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, "update hrm.employment_events set reason_code = 'replacement' where employment_id = $1", Ct, a));
        Assert.Equal("ck_employment_events_fixed", edit.ConstraintName);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, "delete from hrm.employment_events where employment_id = $1", Ct, a));
        Assert.Equal("ck_employment_events_fixed", delete.ConstraintName);

        var circle = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, "update hrm.employment_assignments set manager_employment_id = $2 where employment_id = $1", Ct, a, b));
        Assert.Equal("ck_employment_assignments_manager_cycle", circle.ConstraintName);
        var shares = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, """update hrm.employment_assignments set cost_allocation = '[{"cost_center_ref": "A", "pct": 0.5}]' where employment_id = $1""", Ct, a));
        Assert.Equal("ck_employment_assignments_cost_allocation_sum", shares.ConstraintName);
        var both = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, """update hrm.employment_assignments set cost_allocation = '[{"cost_center_ref": "A", "department": "x", "pct": 1}]' where employment_id = $1""", Ct, a));
        Assert.Equal("ck_employment_assignments_cost_allocation_sum", both.ConstraintName);
    }
}
