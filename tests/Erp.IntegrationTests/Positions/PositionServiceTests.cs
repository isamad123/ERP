#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Npgsql;

namespace Erp.IntegrationTests.Positions;

/// <summary>
/// The position services of slice S1.22a (Build items 1 to 6) through the real API pipeline, the real access engine, the real workflow
/// engine and a real PostgreSQL: positions created, changed from a date, frozen and closed (FR-HRM-POS-02), the planning model
/// (FR-HRM-POS-01), the vacancy rule with strength exceptions in both models (BR-04, FR-XM-32), grades and headcount budgets, and the
/// department-head and position approver directory (FR-KRN-WFL-02).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PositionServiceTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        _h.Clock.Reset();

        // The events of this test's hires are marked published: the event dispatcher takes the oldest 100 unpublished events of the
        // customer per pass, and other test classes (the contract reminders of S1.20c) dispatch once and look for their own event.
        if (_companies.Count > 0)
        {
            await _h.ExecAsync("""
                update hrm.outbox_messages set published_at = now()
                where published_at is null and aggregate_id in (select id from hrm.employments where company_id = any($1))
                """, CancellationToken.None, _companies.ToArray());
        }
    }

    private readonly List<Guid> _companies = [];

    private async Task<Guid> PersonAsync() => (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;

    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync(string? model = null)
    {
        var company = await _h.CompanyAsync(Ct);
        _companies.Add(company);
        if (model is not null)
        {
            await ModelAsync(company, model);
        }

        return (company, await _h.PlaceAsync(company, Ct));
    }

    private async Task ModelAsync(Guid company, string model)
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var set = await _h.PutAsync(admin, "/api/cfg/settings/hrm.org.model", new { layer = "company", scopeId = company, value = model, reason = "test" }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    private static object HireBody(Guid company, (Guid OrgUnit, Guid Branch) place, Guid person, string joining, Guid? position = null, Guid? grade = null, Guid? unit = null, decimal? fte = null) => new
    {
        personId = person,
        companyId = company,
        employeeType = "permanent",
        joiningDate = joining,
        assignment = new { orgUnitId = unit ?? place.OrgUnit, branchId = place.Branch, title = "Officer", positionId = position, gradeId = grade, fte },
    };

    private async Task<Answer> HireAsync(Guid user, Guid company, (Guid OrgUnit, Guid Branch) place, Guid? position = null, Guid? grade = null, Guid? unit = null, string? joining = null, decimal? fte = null) =>
        await _h.PostAsync(user, Employments, HireBody(company, place, await PersonAsync(), joining ?? Iso(Today.AddDays(-200)), position, grade, unit, fte), Ct);

    private async Task<Guid> HiredAsync(Guid user, Guid company, (Guid OrgUnit, Guid Branch) place, Guid? position = null, Guid? grade = null, string? joining = null)
    {
        var answer = await HireAsync(user, company, place, position, grade, joining: joining);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> PositionAsync(Guid user, Guid company, Guid unit, string code, decimal fte = 1m, string? from = null, Guid? reportsTo = null, Guid? grade = null)
    {
        var made = await _h.PostAsync(user, "/api/hrm/positions", new { companyId = company, positionCode = code, title = "Title " + code, orgUnitId = unit, budgetedFte = fte, validFrom = from ?? Iso(Today.AddDays(-365)), reportsToPositionId = reportsTo, gradeId = grade }, Ct);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        return made.Json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> GradeAsync(Guid user, Guid company, string code)
    {
        var made = await _h.PostAsync(user, "/api/hrm/grades", new { companyId = company, code, nameI18n = new { en = code }, rank = 1, defaults = new { leaveEntitlementKey = "annual" } }, Ct);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        return made.Json.GetProperty("id").GetGuid();
    }

    private Task<long> VersionAsync(Guid employment) => _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment);

    private async Task SetNoApprovalAsync(Guid company)
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = Array.Empty<string>(), reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var approved = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    private async Task<Answer> PlaceAsync(Guid user, Guid employment, Guid? position = null, Guid? grade = null, Guid? unit = null, string? date = null) =>
        await _h.PostAsync(user, $"{Employments}/{employment}/events", new
        {
            kind = "transfer",
            reasonCode = "business_need",
            effectiveDate = date ?? Iso(Today.AddDays(-10)),
            assignment = new { positionId = position, gradeId = grade, orgUnitId = unit },
            rowVersion = await VersionAsync(employment),
        }, Ct);

    private async Task<Guid> ApprovedExceptionAsync(Guid requester, Guid approver, Guid employment, Guid unit, Guid? position, Guid? grade, string date)
    {
        var asked = await _h.PostAsync(requester, "/api/hrm/strength-exceptions", new { employmentId = employment, orgUnitId = unit, positionId = position, gradeId = grade, effectiveDate = date, reason = "Cover for a long absence" }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, asked.Json.GetProperty("instanceId").GetGuid());
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(requester, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status); // FR-XM-32
        var approved = await _h.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        return asked.Json.GetProperty("id").GetGuid();
    }

    // ------------------------------------------------------------------ positions

    [Fact]
    public async Task FR_HRM_POS_02_a_position_is_created_changed_from_a_date_frozen_and_closed()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("position");
        var start = Iso(Today.AddDays(-300));
        var id = await PositionAsync(hr, company, place.OrgUnit, "AN-1", 2m, start);

        // The code is the position's identity: taken once per company, in any case.
        var again = await _h.PostAsync(hr, "/api/hrm/positions", new { companyId = company, positionCode = "an-1", title = "Again", orgUnitId = place.OrgUnit, validFrom = start }, Ct);
        Assert.Equal("errors.hrm.position.code_taken", again.Json.GetProperty("errors").GetProperty("positionCode")[0].GetString());
        Assert.Equal("errors.hrm.field.required", (await _h.PostAsync(hr, "/api/hrm/positions", new { companyId = company }, Ct)).Json.GetProperty("errors").GetProperty("positionCode")[0].GetString());

        // A change from a date adds a row and ends the old one the day before; the other values carry over.
        var changeDate = Iso(Today.AddDays(-100));
        var version = await _h.ScalarAsync<long>("select row_version from hrm.positions where id = $1", Ct, id);
        var changed = await _h.PostAsync(hr, $"/api/hrm/positions/{id}/changes", new { effectiveDate = changeDate, title = "Senior analyst", budgetedFte = 3m, rowVersion = version }, Ct);
        Assert.True(changed.Status == HttpStatusCode.Created, changed.Body);
        var next = changed.Json.GetProperty("id").GetGuid();
        Assert.Equal(changeDate, Iso(await _h.ScalarAsync<DateOnly>("select valid_to from hrm.positions where id = $1", Ct, id)));
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from hrm.positions where company_id = $1 and position_code_lower = 'an-1'", Ct, company));
        var before = await _h.GetAsync(hr, $"/api/hrm/positions?companyId={company}&asOf={Iso(Today.AddDays(-200))}", Ct);
        Assert.Equal(["Title AN-1"], before.Json.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("title").GetString()));
        var after = await _h.GetAsync(hr, $"/api/hrm/positions?companyId={company}&asOf={Iso(Today)}", Ct);
        Assert.Equal(["Senior analyst"], after.Json.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("title").GetString()));
        Assert.Equal(3m, after.Json.GetProperty("items")[0].GetProperty("budgetedFte").GetDecimal());

        // Only the latest row changes; a stale version and a change with nothing new are refused.
        Assert.Equal("errors.hrm.position.not_latest", (await _h.PostAsync(hr, $"/api/hrm/positions/{id}/changes", new { effectiveDate = Iso(Today), title = "x", rowVersion = version + 1 }, Ct)).Text("title"));
        Assert.Equal("errors.data.concurrency_conflict", (await _h.PostAsync(hr, $"/api/hrm/positions/{next}/changes", new { effectiveDate = Iso(Today), title = "x", rowVersion = 99 }, Ct)).Text("title"));
        Assert.Equal("errors.hrm.position.nothing_changes", (await _h.PostAsync(hr, $"/api/hrm/positions/{next}/changes", new { effectiveDate = Iso(Today), title = "Senior analyst", rowVersion = 1 }, Ct)).Text("title"));

        // Freeze, holders and the status derived from them: open, filled at the budget, planned before its first day.
        var holders = await _h.GetAsync(hr, $"/api/hrm/positions/{next}/holders", Ct);
        Assert.Equal("open", holders.Text("status"));
        Assert.Equal(3m, holders.Json.GetProperty("vacantFte").GetDecimal());
        await SetNoApprovalAsync(company);
        for (var i = 0; i < 3; i++)
        {
            await HiredAsync(hr, company, place, next, joining: Iso(Today.AddDays(-50))); // after the budget rose to 3
        }

        holders = await _h.GetAsync(hr, $"/api/hrm/positions/{next}/holders", Ct);
        Assert.Equal("filled", holders.Text("status"));
        Assert.Equal(3m, holders.Json.GetProperty("filledFte").GetDecimal());
        Assert.Equal(3, holders.Json.GetProperty("holders").GetArrayLength());
        Assert.Equal("planned", (await _h.GetAsync(hr, $"/api/hrm/positions/{next}/holders?asOf={Iso(Today.AddDays(-400))}", Ct)).Text("status"));

        // Closing is refused while people hold the position, allowed once they do not; a closed position takes nobody.
        var open = await _h.ScalarAsync<long>("select row_version from hrm.positions where id = $1", Ct, next);
        var refused = await _h.PostAsync(hr, $"/api/hrm/positions/{next}/close", new { effectiveDate = Iso(Today.AddDays(30)), rowVersion = open }, Ct);
        Assert.Equal("errors.hrm.position.has_holders", refused.Text("title"));
        var empty = await PositionAsync(hr, company, place.OrgUnit, "AN-2");
        var emptyVersion = await _h.ScalarAsync<long>("select row_version from hrm.positions where id = $1", Ct, empty);
        var frozen = await _h.PostAsync(hr, $"/api/hrm/positions/{empty}/changes", new { effectiveDate = Iso(Today.AddDays(-1)), status = "frozen", rowVersion = emptyVersion }, Ct);
        Assert.True(frozen.Status == HttpStatusCode.Created, frozen.Body);
        var frozenId = frozen.Json.GetProperty("id").GetGuid();
        Assert.Equal("frozen", (await _h.GetAsync(hr, $"/api/hrm/positions/{frozenId}/holders", Ct)).Text("status"));
        Assert.Equal("errors.hrm.position.frozen", (await HireAsync(hr, company, place, frozenId, joining: Iso(Today))).Text("title")); // nobody is placed in a frozen position
        var closed = await _h.PostAsync(hr, $"/api/hrm/positions/{frozenId}/close", new { effectiveDate = Iso(Today.AddDays(5)), rowVersion = 1 }, Ct);
        Assert.True(closed.Status == HttpStatusCode.OK, closed.Body);
        Assert.Equal("closed", closed.Text("status"));
        Assert.Equal("errors.hrm.position.closed", (await _h.PostAsync(hr, $"/api/hrm/positions/{frozenId}/close", new { effectiveDate = Iso(Today.AddDays(9)), rowVersion = 2 }, Ct)).Text("title"));
    }

    [Fact]
    public async Task FR_HRM_POS_02_a_position_cannot_report_to_itself_through_others_and_every_write_needs_its_permission_and_scope()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("position");
        var a = await PositionAsync(hr, company, place.OrgUnit, "BOSS");
        var b = await PositionAsync(hr, company, place.OrgUnit, "MID", reportsTo: a);
        var c = await PositionAsync(hr, company, place.OrgUnit, "LOW", reportsTo: b);

        // The database refuses a circle: BOSS reports to LOW, who reports to MID, who reports to BOSS.
        var version = await _h.ScalarAsync<long>("select row_version from hrm.positions where id = $1", Ct, a);
        var circle = await _h.PostAsync(hr, $"/api/hrm/positions/{a}/changes", new { effectiveDate = Iso(Today), reportsToPositionId = c, rowVersion = version }, Ct);
        Assert.Equal("errors.hrm.position.reporting_cycle", circle.Json.GetProperty("errors").GetProperty("reportsToPositionId")[0].GetString());
        var itself = await _h.PostAsync(hr, $"/api/hrm/positions/{a}/changes", new { effectiveDate = Iso(Today), reportsToPositionId = a, rowVersion = version }, Ct);
        Assert.Equal("errors.hrm.position.reporting_cycle", itself.Json.GetProperty("errors").GetProperty("reportsToPositionId")[0].GetString());

        // No permission, no write (a line manager only reads); a position of a company the user does not cover is out of scope.
        var line = await _h.UserAsync(Ct, null, "line_manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(line, "/api/hrm/positions", new { companyId = company, positionCode = "X", title = "X", orgUnitId = place.OrgUnit }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(line, $"/api/hrm/positions/{a}/changes", new { effectiveDate = Iso(Today), title = "X", rowVersion = version }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(line, "/api/hrm/strength-exceptions", new { employmentId = Guid.NewGuid() }, Ct)).Status);
        var elsewhere = await _h.UserAsync(Ct);
        await _h.AssignAsync(elsewhere, await _h.RoleIdAsync("hr_admin", Ct), await _h.CompanyAsync(Ct), Today.AddDays(-1), null, Ct);
        var outOfScope = await _h.PostAsync(elsewhere, "/api/hrm/positions", new { companyId = company, positionCode = "OUT", title = "Out", orgUnitId = place.OrgUnit }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, outOfScope.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(elsewhere, $"/api/hrm/positions/{a}/changes", new { effectiveDate = Iso(Today), title = "X", rowVersion = version }, Ct)).Status);
    }

    // ------------------------------------------------------------------ vacancy rule

    [Fact]
    public async Task BR_04_FR_HRM_POS_01_FR_XM_32_in_a_position_company_a_second_person_beyond_the_budget_is_refused_until_an_approved_exception_names_the_event()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("position");
        await SetNoApprovalAsync(company);
        var position = await PositionAsync(hr, company, place.OrgUnit, "FIN-HEAD", 1m);

        // Budgeted FTE 1 filled by one person shows "filled"; the second is refused as over budget.
        await HiredAsync(hr, company, place, position);
        Assert.Equal("filled", (await _h.GetAsync(hr, $"/api/hrm/positions/{position}/holders", Ct)).Text("status"));
        var refused = await HireAsync(hr, company, place, position);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("errors.hrm.strength.over_budget", refused.Text("title"));
        Assert.Equal(1m, refused.Json.GetProperty("filledFte").GetDecimal());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employments where company_id = $1", Ct, company) - 1);

        // A person placed later through a change is refused the same way, and refused again without an exception for this person.
        var second = await HiredAsync(hr, company, place);
        var third = await HiredAsync(hr, company, place);
        var date = Iso(Today.AddDays(-10));
        Assert.Equal("errors.hrm.strength.over_budget", (await PlaceAsync(hr, second, position, date: date)).Text("title"));
        var check = await _h.PostAsync(hr, $"{Employments}/{second}/events/check", new { kind = "transfer", reasonCode = "business_need", effectiveDate = date, assignment = new { positionId = position } }, Ct);
        Assert.Equal("errors.hrm.strength.over_budget", check.Text("title"));

        // The exception: requested, decided by someone else (never the requester), covers this person, this place and this day only.
        var exception = await ApprovedExceptionAsync(hr, checker, second, place.OrgUnit, position, null, date);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.strength_exceptions where id = $1", Ct, exception));
        Assert.Equal("errors.hrm.strength.over_budget", (await PlaceAsync(hr, third, position, date: date)).Text("title"));
        Assert.Equal("errors.hrm.strength.over_budget", (await PlaceAsync(hr, second, position, date: Iso(Today.AddDays(-9)))).Text("title"));
        var placed = await PlaceAsync(hr, second, position, date: date);
        Assert.True(placed.Status == HttpStatusCode.Created, placed.Body);

        // The event names the exception and the exception names the event; it cannot be used twice.
        var eventId = placed.Json.GetProperty("eventId").GetGuid();
        Assert.Equal(exception.ToString(), await _h.ScalarAsync<string>("select changes ->> 'strength_exception' from hrm.employment_events where id = $1", Ct, eventId));
        Assert.Equal(("used", eventId), (await _h.ScalarAsync<string>("select status from hrm.strength_exceptions where id = $1", Ct, exception), await _h.ScalarAsync<Guid>("select used_event_id from hrm.strength_exceptions where id = $1", Ct, exception)));
        var holders = await _h.GetAsync(hr, $"/api/hrm/positions/{position}/holders", Ct);
        Assert.Equal(2m, holders.Json.GetProperty("filledFte").GetDecimal());
        Assert.Equal(1m, holders.Json.GetProperty("overFte").GetDecimal());
        Assert.Equal("filled", holders.Text("status"));
        Assert.Equal("errors.hrm.strength.over_budget", (await PlaceAsync(hr, third, position, date: date)).Text("title"));

        // An unrelated change of the person already over the budget is not refused (the place did not change).
        var title = await _h.PostAsync(hr, $"{Employments}/{second}/events", new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today.AddDays(-5)), assignment = new { title = "Analyst" }, rowVersion = await VersionAsync(second) }, Ct);
        Assert.True(title.Status == HttpStatusCode.Created, title.Body);
    }

    [Fact]
    public async Task BR_04_FR_HRM_POS_01_in_a_headcount_company_the_sixth_analyst_beyond_five_sanctioned_is_refused_and_an_exception_lets_one_through()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        await SetNoApprovalAsync(company);
        var analyst = await GradeAsync(hr, company, "ANALYST");
        var other = await GradeAsync(hr, company, "MANAGER");
        var budget = await _h.PostAsync(hr, "/api/hrm/headcount-budgets", new { companyId = company, orgUnitId = place.OrgUnit, gradeId = analyst, sanctionedCount = 5, validFrom = Iso(Today.AddDays(-400)) }, Ct);
        Assert.True(budget.Status == HttpStatusCode.Created, budget.Body);

        // Sanctioned 5 analysts, 5 hired, the sixth refused; another grade of the unit has no budget and is not limited.
        for (var i = 0; i < 5; i++)
        {
            await HiredAsync(hr, company, place, grade: analyst);
        }

        var sixth = await HireAsync(hr, company, place, grade: analyst);
        Assert.Equal("errors.hrm.strength.over_sanctioned", sixth.Text("title"));
        Assert.Equal(5, sixth.Json.GetProperty("sanctionedCount").GetInt32());
        Assert.Equal(5, sixth.Json.GetProperty("filledCount").GetInt32());
        await HiredAsync(hr, company, place, grade: other);

        // An exception covers one placement of one person; the hire event of a person already hired names it on a change.
        var candidate = await HiredAsync(hr, company, place, grade: other);
        var date = Iso(Today.AddDays(-3));
        Assert.Equal("errors.hrm.strength.over_sanctioned", (await PlaceAsync(hr, candidate, grade: analyst, date: date)).Text("title"));
        var exception = await ApprovedExceptionAsync(hr, checker, candidate, place.OrgUnit, null, analyst, date);
        var placed = await PlaceAsync(hr, candidate, grade: analyst, date: date);
        Assert.True(placed.Status == HttpStatusCode.Created, placed.Body);
        Assert.Equal(exception.ToString(), await _h.ScalarAsync<string>("select changes ->> 'strength_exception' from hrm.employment_events where id = $1", Ct, placed.Json.GetProperty("eventId").GetGuid()));

        // A unit-wide sanctioned count (no grade) limits everybody in the unit, whatever the grade.
        var wide = await _h.PostAsync(hr, "/api/hrm/headcount-budgets", new { companyId = company, orgUnitId = place.OrgUnit, sanctionedCount = 7, validFrom = Iso(Today.AddDays(-400)) }, Ct);
        Assert.True(wide.Status == HttpStatusCode.Created, wide.Body);
        Assert.Equal("errors.hrm.strength.over_sanctioned", (await HireAsync(hr, company, place, grade: other)).Text("title"));

        // Negative counts and overlapping periods are refused by the database.
        var negative = await _h.PostAsync(hr, "/api/hrm/headcount-budgets", new { companyId = company, orgUnitId = place.OrgUnit, gradeId = other, sanctionedCount = -1, validFrom = Iso(Today) }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, negative.Status);
        var overlap = await _h.PostAsync(hr, "/api/hrm/headcount-budgets", new { companyId = company, orgUnitId = place.OrgUnit, gradeId = analyst, sanctionedCount = 9, validFrom = Iso(Today) }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, overlap.Status);
    }

    // ------------------------------------------------------------------ model and grades

    [Fact]
    public async Task FR_HRM_POS_01_the_planning_model_changes_only_when_the_assignments_allow_it_and_the_screen_data_says_what_is_needed()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var (company, place) = await CompanyAsync("position");
        var position = await PositionAsync(hr, company, place.OrgUnit, "ONE", 2m);
        await SetNoApprovalAsync(company);

        // Position to headcount only while no assignment names a position.
        var employment = await HiredAsync(hr, company, place, position);
        var models = await _h.GetAsync(hr, "/api/hrm/org-model", Ct);
        var mine = models.Json.EnumerateArray().Single(m => m.GetProperty("companyId").GetGuid() == company);
        Assert.Equal("position", mine.GetProperty("model").GetString());
        Assert.Equal(1, mine.GetProperty("assignmentsWithPosition").GetInt32());
        Assert.False(mine.GetProperty("canSwitchToHeadcount").GetBoolean());
        var refused = await _h.PutAsync(admin, "/api/cfg/settings/hrm.org.model", new { layer = "company", scopeId = company, value = "headcount", reason = "plan by numbers" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.hrm.org_model.assignments_have_positions", refused.Text("title"));

        // Once nobody holds a position the switch goes through; back to position only when every active assignment has one.
        var emptied = await _h.PostAsync(hr, $"{Employments}/{employment}/events", new { kind = "transfer", reasonCode = "business_need", effectiveDate = Iso(Today.AddDays(-5)), assignment = new { clear = new[] { "positionId" } }, rowVersion = await VersionAsync(employment) }, Ct);
        Assert.True(emptied.Status == HttpStatusCode.Created, emptied.Body);

        await ModelAsync(company, "headcount");
        var back = await _h.PutAsync(admin, "/api/cfg/settings/hrm.org.model", new { layer = "company", scopeId = company, value = "position", reason = "plan by posts" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, back.Status);
        Assert.Equal("errors.hrm.org_model.assignments_lack_positions", back.Text("title"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PutAsync(admin, "/api/cfg/settings/hrm.org.model", new { layer = "company", scopeId = company, value = "nonsense", reason = "x" }, Ct)).Status);

        // A model that no assignment contradicts is never refused, and the default is headcount.
        var (fresh, _) = await CompanyAsync();
        await ModelAsync(fresh, "position");
        await ModelAsync(fresh, "headcount");
        Assert.Equal("headcount", (await _h.GetAsync(hr, "/api/hrm/org-model", Ct)).Json.EnumerateArray().Single(m => m.GetProperty("companyId").GetGuid() == fresh).GetProperty("model").GetString());
    }

    [Fact]
    public async Task FR_HRM_POS_06_grades_keep_a_rank_and_named_defaults_and_the_database_refuses_a_negative_rank_or_defaults_that_are_not_an_object()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, _) = await CompanyAsync();
        var grade = await GradeAsync(hr, company, "G-7");
        var listed = await _h.GetAsync(hr, $"/api/hrm/grades?companyId={company}", Ct);
        Assert.Equal("annual", listed.Json.GetProperty("items")[0].GetProperty("defaults").GetProperty("leaveEntitlementKey").GetString());
        var negative = await _h.PostAsync(hr, "/api/hrm/grades", new { companyId = company, code = "NEG", nameI18n = new { en = "n" }, rank = -1, defaults = new { } }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, negative.Status);
        var notObject = await _h.PostAsync(hr, "/api/hrm/grades", new { companyId = company, code = "ARR", nameI18n = new { en = "n" }, rank = 1, defaults = new[] { 1 } }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, notObject.Status);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.grades where id = $1", Ct, grade);
        var updated = await _h.PutAsync(hr, $"/api/hrm/grades/{grade}", new { companyId = company, code = "G-7", nameI18n = new { en = "G-7" }, rank = 4, defaults = new { leaveEntitlementKey = "annual" }, rowVersion = version }, Ct);
        Assert.True(updated.Status == HttpStatusCode.OK, updated.Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(await _h.UserAsync(Ct, null, "line_manager"), "/api/hrm/grades", new { companyId = company, code = "NO", nameI18n = new { en = "n" }, rank = 1, defaults = new { } }, Ct)).Status);
    }

    // ------------------------------------------------------------------ approver directory

    [Fact]
    public async Task FR_KRN_WFL_02_FR_XM_32_a_department_head_is_the_holder_of_the_head_position_on_the_date_walking_up_when_there_is_none_or_the_asker_is_the_head()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("position");
        await SetNoApprovalAsync(company);
        var org = await HierarchyAsync();
        var division = place.OrgUnit;
        var department = await UnitAsync(company, org, "DEPT", division);
        var team = await UnitAsync(company, org, "TEAM", department);
        var lone = await UnitAsync(company, org, "LONE", null);
        var divisionHead = await PositionAsync(hr, company, division, "HEAD-DIV");
        var departmentHead = await PositionAsync(hr, company, department, "HEAD-DEPT");
        await _h.ExecAsync("update org.org_units set head_position_ref = $2 where id = $1", Ct, division, divisionHead);
        await _h.ExecAsync("update org.org_units set head_position_ref = $2 where id = $1", Ct, department, departmentHead);

        var (divUser, deptUser, staffUser, loneUser) = (await _h.UserAsync(Ct), await _h.UserAsync(Ct), await _h.UserAsync(Ct), await _h.UserAsync(Ct));
        await LinkedAsync(hr, company, place, divUser, divisionHead, division);
        await LinkedAsync(hr, company, place, deptUser, departmentHead, department);
        await LinkedAsync(hr, company, place, staffUser, null, team);
        await LinkedAsync(hr, company, place, loneUser, null, lone);

        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(fx.AdminConnectionString) { Database = Erp.Kernel.Infrastructure.ControlPlane.TenantLogins.DatabaseName(_h.Code), Pooling = false }.ConnectionString);
        var directory = new Erp.Hrm.Infrastructure.Employments.HrmApproverDirectory(new BoundTenant(source));
        Assert.True(directory.Supports("department_head"));
        Assert.True(directory.Supports("position"));

        // A team without a head goes up to its department's head; the department head asking goes up to the division's head.
        Assert.Equal([deptUser], await directory.DepartmentHeadsAsync(staffUser, company, Today, Ct));
        Assert.Equal([divUser], await directory.DepartmentHeadsAsync(deptUser, company, Today, Ct));
        // The division head is the head of the top: nobody above, so no answer (the engine's escalation applies); same for a unit with no chain.
        Assert.Empty(await directory.DepartmentHeadsAsync(divUser, company, Today, Ct));
        Assert.Empty(await directory.DepartmentHeadsAsync(loneUser, company, Today, Ct));
        // On the date: before the people joined nothing resolves; a person of another company or without an employment has none.
        Assert.Empty(await directory.DepartmentHeadsAsync(staffUser, company, Today.AddDays(-500), Ct));
        Assert.Empty(await directory.DepartmentHeadsAsync(staffUser, Guid.NewGuid(), Today, Ct));
        Assert.Empty(await directory.DepartmentHeadsAsync(Guid.NewGuid(), company, Today, Ct));

        // Positions by code (any case), on the date, with a change of the position's row not losing the holder.
        Assert.Equal([deptUser], await directory.PositionHoldersAsync("head-dept", company, Today, Ct));
        Assert.Empty(await directory.PositionHoldersAsync("head-dept", company, Today.AddDays(-500), Ct));
        Assert.Empty(await directory.PositionHoldersAsync("NO-SUCH", company, Today, Ct));
        Assert.Empty(await directory.PositionHoldersAsync("HEAD-DEPT", Guid.NewGuid(), Today, Ct));
        var version = await _h.ScalarAsync<long>("select row_version from hrm.positions where id = $1", Ct, departmentHead);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(hr, $"/api/hrm/positions/{departmentHead}/changes", new { effectiveDate = Iso(Today.AddDays(-1)), title = "Head of department", rowVersion = version }, Ct)).Status);
        Assert.Equal([deptUser], await directory.PositionHoldersAsync("HEAD-DEPT", company, Today, Ct));
        Assert.Equal([deptUser], await directory.DepartmentHeadsAsync(staffUser, company, Today, Ct));
    }

    /// <summary>The customer database for a directory built outside a request (the API binds it from the host name).</summary>
    private sealed class BoundTenant(NpgsqlDataSource source) : Erp.BuildingBlocks.Application.Tenancy.ITenantConnectionProvider
    {
        public System.Data.Common.DbDataSource DataSource => source;
    }

    private async Task<Guid> LinkedAsync(Guid hr, Guid company, (Guid OrgUnit, Guid Branch) place, Guid user, Guid? position, Guid unit)
    {
        var answer = await HireAsync(hr, company, place, position, unit: unit);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var id = answer.Json.GetProperty("id").GetGuid();
        var linked = await _h.PutAsync(hr, $"{Employments}/{id}/user", new { userId = user, rowVersion = answer.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.OK, linked.Status);
        return id;
    }

    private async Task<Guid> HierarchyAsync()
    {
        var hierarchy = Guid.CreateVersion7();
        var n = Guid.NewGuid().ToString("N")[..8];
        await _h.ExecAsync("""
            insert into org.org_hierarchies (id, code, name_i18n, kind, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Reporting"}', 'reporting', now(), $3, now(), $3)
            """, Ct, hierarchy, "PH" + n, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        return hierarchy;
    }

    private async Task<Guid> UnitAsync(Guid company, Guid hierarchy, string code, Guid? parent)
    {
        var id = Guid.CreateVersion7();
        var system = Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId;
        var type = await _h.ScalarAsync<Guid>("select unit_type_id from org.org_units where company_id = $1 limit 1", Ct, company);
        await _h.ExecAsync("""
            insert into org.org_units (id, company_id, code, name_i18n, unit_type_id, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4::jsonb, $5, now(), $6, now(), $6)
            """, Ct, id, company, code + "-" + id.ToString("N")[..6], "{\"en\":\"" + code + "\"}", type, system);
        await _h.ExecAsync("""
            insert into org.org_unit_links (id, hierarchy_id, unit_id, parent_unit_id, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, '2020-01-01', now(), $5, now(), $5)
            """, Ct, Guid.CreateVersion7(), hierarchy, id, parent, system);
        return id;
    }

    // ------------------------------------------------------------------ exceptions

    [Fact]
    public async Task FR_XM_32_a_strength_exception_is_decided_by_another_person_and_a_rejected_one_lets_nothing_through()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("position");
        await SetNoApprovalAsync(company);
        var position = await PositionAsync(hr, company, place.OrgUnit, "SOLO", 1m);
        await HiredAsync(hr, company, place, position);
        var other = await HiredAsync(hr, company, place);
        var date = Iso(Today.AddDays(-2));

        var asked = await _h.PostAsync(hr, "/api/hrm/strength-exceptions", new { employmentId = other, orgUnitId = place.OrgUnit, positionId = position, effectiveDate = date, reason = "Temporary cover" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        Assert.Equal("pending_approval", asked.Text("status"));
        Assert.Equal("errors.hrm.strength.over_budget", (await PlaceAsync(hr, other, position, date: date)).Text("title")); // pending is not approved
        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, asked.Json.GetProperty("instanceId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/wf/tasks/{task}/reject", new { comment = "no" }, Ct)).Status);
        Assert.Equal("rejected", await _h.ScalarAsync<string>("select status from hrm.strength_exceptions where id = $1", Ct, asked.Json.GetProperty("id").GetGuid()));
        Assert.Equal("errors.hrm.strength.over_budget", (await PlaceAsync(hr, other, position, date: date)).Text("title"));

        // Required fields, a reason and a person who exists.
        var empty = await _h.PostAsync(hr, "/api/hrm/strength-exceptions", new { }, Ct);
        Assert.Equal(["effectiveDate", "employmentId", "orgUnitId", "reason"], empty.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(hr, "/api/hrm/strength-exceptions", new { employmentId = Guid.NewGuid(), orgUnitId = place.OrgUnit, effectiveDate = date, reason = "x" }, Ct)).Status);
    }
}
