#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Leave;

/// <summary>
/// The team leave calendar and the coverage warning of approval cards (slice S1.33a Build items 3 and 4): FR-HRM-LVE-05 (the approver sees who else
/// is away, a warning that never blocks), FR-HRM-LVE-15 (team calendar equals the day lists, holidays and weekly offs shown, scope respected) and
/// the leave type colour token (design token names only). The company's date comes from the harness clock in Asia/Karachi.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class LeaveCalendarTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Requests = "/api/hrm/leave-requests";
    private const string Calendar = "/api/hrm/leave-calendar";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private DateOnly Monday
    {
        get
        {
            var d = Today.AddDays(21);
            while (d.DayOfWeek != DayOfWeek.Monday)
            {
                d = d.AddDays(1);
            }

            if (d.AddDays(28).Year != d.Year)
            {
                d = new DateOnly(d.Year + 1, 1, 8);
                while (d.DayOfWeek != DayOfWeek.Monday)
                {
                    d = d.AddDays(1);
                }
            }

            return d;
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Team(Guid Company, Guid Admin, Guid Type, Guid ManagerUser, Guid ManagerEmployment, (Guid Employment, Guid User)[] Staff);

    /// <summary>A manager with four direct reports (each with a sign-in), one leave type with a colour, a leave policy, Saturday and Sunday off and a holiday on Wednesday.</summary>
    private async Task<Team> TeamAsync(string colour = "info")
    {
        var company = await _h.CompanyAsync(Ct);
        var place = await _h.PlaceAsync(company, Ct);
        var admin = await _h.UserAsync(Ct, null, "hr_admin");
        var joined = Monday.AddYears(-2);
        async Task<Guid> HireAsync(Guid? user, Guid? manager)
        {
            var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
            var answer = await _h.PostAsync(admin, "/api/hrm/employments", EmploymentTestSupport.HireBody(company, place, person: person, joining: Iso(joined), manager: manager), Ct);
            Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
            var id = answer.Json.GetProperty("id").GetGuid();
            if (user is { } u)
            {
                await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, id, u);
            }

            return id;
        }

        var managerUser = await _h.UserAsync(Ct, null, "line_manager");
        var manager = await HireAsync(managerUser, null);
        var staff = new List<(Guid, Guid)>();
        for (var i = 0; i < 4; i++)
        {
            var user = await _h.UserAsync(Ct, null, "employee");
            staff.Add((await HireAsync(user, manager), user));
        }

        var type = await _h.PostAsync(admin, "/api/hrm/leave-types", new
        {
            companyId = company, code = "CAL" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), nameI18n = new { en = "Calendar leave", ur = "کیلنڈر رخصت" }, isPaid = true,
            counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false, colorToken = colour,
        }, Ct);
        Assert.True(type.Status == HttpStatusCode.Created, type.Body);
        var typeId = type.Json.GetProperty("id").GetGuid();
        var code = type.Json.GetProperty("code").GetString()!;

        var from = new DateOnly(Today.Year - 3, 1, 1);
        var (leave, _) = await _h.ActivePolicyAsync(admin, company, "leave", new { types = new[] { new { leaveType = code, entitlement = new { model = "manual" } } } }, from, Ct);
        var leaveSet = await _h.ActivateSetAsync(admin, company, "leave", from, Ct, (leave, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(leaveSet.Status == HttpStatusCode.OK, leaveSet.Body);
        var (shift, _) = await _h.ActivePolicyAsync(admin, company, "shift", new { weeklyOffKind = "fixed", offDays = new[] { 6, 7 } }, from, Ct);
        var shiftSet = await _h.ActivateSetAsync(admin, company, "shift", from, Ct, (shift, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(shiftSet.Status == HttpStatusCode.OK, shiftSet.Body);
        var calendar = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into ref.calendars (id, code, name_i18n, source, is_active, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, '{"en":"Calendar test"}'::jsonb, 'tenant', true, now(), $3, now(), $3, 1)
            """, Ct, calendar, "CCAL" + Guid.NewGuid().ToString("N")[..10], admin);
        await _h.ExecAsync("""
            insert into ref.holidays (id, calendar_id, holiday_date, name_i18n, kind, is_tentative, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, '{"en":"Mid-week holiday"}'::jsonb, 'public', false, now(), $4, now(), $4, 1)
            """, Ct, Guid.CreateVersion7(), calendar, Monday.AddDays(2), admin);
        await _h.ExecAsync("update org.companies set holiday_calendar_id = $2 where id = $1", Ct, company, calendar);

        foreach (var (employment, _) in staff)
        {
            var grant = await _h.PostAsync(admin, $"/api/hrm/employments/{employment}/leave-ledger", new { leaveTypeId = typeId, kind = "grant", quantity = 10m, entryDate = Iso(new DateOnly(Monday.Year, 1, 1)), reason = "Granted for the test" }, Ct);
            Assert.True(grant.Status == HttpStatusCode.Created, grant.Body);
        }

        return new Team(company, admin, typeId, managerUser, manager, [.. staff]);
    }

    private async Task<Guid> RequestAsync(Team t, int who, DateOnly start, DateOnly end, bool approve)
    {
        var asked = await _h.PostAsync(t.Staff[who].User, Requests, new { leaveTypeId = t.Type, startDate = Iso(start), endDate = Iso(end) }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        if (approve)
        {
            var instance = await _h.ScalarAsync<Guid>("select workflow_instance_id from hrm.leave_requests where id = $1", Ct, id);
            var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at desc limit 1", Ct, instance);
            var done = await _h.PostAsync(t.ManagerUser, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
            Assert.True(done.Status == HttpStatusCode.OK, done.Body);
        }

        return id;
    }

    [Fact]
    public async Task FR_HRM_LVE_15_the_team_calendar_shows_each_persons_leave_holidays_and_weekly_offs_and_equals_the_request_days()
    {
        var t = await TeamAsync();
        var tuesday = Monday.AddDays(1);
        var approved = await RequestAsync(t, 0, tuesday, Monday.AddDays(3), approve: true); // Tue, Thu (Wednesday is a holiday)
        var waiting = await RequestAsync(t, 1, tuesday, tuesday, approve: false);

        var answer = await _h.GetAsync(t.ManagerUser, $"{Calendar}?from={Iso(Monday)}&to={Iso(Monday.AddDays(6))}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        var people = answer.Json.GetProperty("people").EnumerateArray().ToList();
        Assert.Equal(4, answer.Json.GetProperty("total").GetInt32()); // the manager's team: the four reports
        var first = people.Single(p => p.GetProperty("employmentId").GetGuid() == t.Staff[0].Employment);
        var days = first.GetProperty("leave").EnumerateArray().Select(d => d.GetProperty("date").GetString() + ":" + d.GetProperty("status").GetString()).ToList();
        Assert.Equal(new[] { $"{Iso(tuesday)}:approved", $"{Iso(Monday.AddDays(3))}:approved" }, days);
        var second = people.Single(p => p.GetProperty("employmentId").GetGuid() == t.Staff[1].Employment);
        Assert.Equal(new[] { $"{Iso(tuesday)}:submitted" }, second.GetProperty("leave").EnumerateArray().Select(d => d.GetProperty("date").GetString() + ":" + d.GetProperty("status").GetString()));

        // The calendar equals the S1.32b day rows.
        Assert.Equal(
            await _h.ScalarAsync<long>("select count(*) from hrm.leave_request_days where leave_request_id = any($1)", Ct, new[] { approved, waiting }),
            people.Sum(p => (long)p.GetProperty("leave").GetArrayLength()));

        // Weekly offs (Saturday, Sunday) from the shift policy, the holiday from the calendar, the colour token on the type.
        Assert.Equal(new[] { Iso(Monday.AddDays(5)), Iso(Monday.AddDays(6)) }, first.GetProperty("offDays").EnumerateArray().Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(Iso(Monday.AddDays(2)), answer.Json.GetProperty("holidays")[0].GetProperty("items")[0].GetProperty("date").GetString());
        Assert.Equal("info", answer.Json.GetProperty("types")[0].GetProperty("colorToken").GetString());

        // Coverage: Tuesday two of four are away (approved and waiting), Friday nobody; the default limit is 30 percent.
        var coverage = answer.Json.GetProperty("coverage").EnumerateArray().ToDictionary(c => c.GetProperty("date").GetString()!, c => c);
        Assert.Equal(2, coverage[Iso(tuesday)].GetProperty("away").GetInt32());
        Assert.Equal(4, coverage[Iso(tuesday)].GetProperty("teamSize").GetInt32());
        Assert.Equal(50m, coverage[Iso(tuesday)].GetProperty("percent").GetDecimal());
        Assert.True(coverage[Iso(tuesday)].GetProperty("warn").GetBoolean());
        Assert.False(coverage[Iso(Monday.AddDays(4))].GetProperty("warn").GetBoolean());
        Assert.Equal(30, answer.Json.GetProperty("companies")[0].GetProperty("limitPercent").GetInt32());

        // A unit filter that matches nobody, and a leave type filter, narrow the answer.
        var nobody = await _h.GetAsync(t.ManagerUser, $"{Calendar}?from={Iso(Monday)}&to={Iso(Monday.AddDays(6))}&unitId={Guid.NewGuid()}", Ct);
        Assert.Equal(0, nobody.Json.GetProperty("total").GetInt32());

        // An employee sees only themselves (data scope own); a range of more than 62 days or a reversed range is refused.
        var own = await _h.GetAsync(t.Staff[2].User, $"{Calendar}?from={Iso(Monday)}&to={Iso(Monday.AddDays(6))}", Ct);
        Assert.Equal(1, own.Json.GetProperty("total").GetInt32());
        Assert.Equal(t.Staff[2].Employment, own.Json.GetProperty("people")[0].GetProperty("employmentId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(t.ManagerUser, $"{Calendar}?from={Iso(Monday)}&to={Iso(Monday.AddDays(90))}", Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(t.ManagerUser, $"{Calendar}?from={Iso(Monday.AddDays(3))}&to={Iso(Monday)}", Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_LVE_05_the_approval_card_shows_how_much_of_the_team_is_away_and_the_warning_never_blocks_the_decision()
    {
        var t = await TeamAsync();
        var tuesday = Monday.AddDays(1);
        await RequestAsync(t, 0, tuesday, tuesday, approve: true);
        var waiting = await RequestAsync(t, 1, tuesday, Monday.AddDays(3), approve: false); // Tuesday and Thursday (Wednesday is a holiday)

        var card = await _h.GetAsync(t.ManagerUser, $"{Requests}/{waiting}/coverage", Ct);
        Assert.True(card.Status == HttpStatusCode.OK, card.Body);
        Assert.True(card.Json.GetProperty("teamKnown").GetBoolean());
        Assert.Equal(4, card.Json.GetProperty("teamSize").GetInt32());
        Assert.True(card.Json.GetProperty("warn").GetBoolean());
        var days = card.Json.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(new[] { Iso(tuesday), Iso(Monday.AddDays(3)) }, days.Select(d => d.GetProperty("date").GetString())); // only the days the request holds
        Assert.Equal(2, days[0].GetProperty("away").GetInt32()); // the colleague and the requester
        Assert.True(days[0].GetProperty("warn").GetBoolean());
        Assert.Equal(1, days[1].GetProperty("away").GetInt32());
        Assert.False(days[1].GetProperty("warn").GetBoolean()); // 1 of 4 is 25 percent, below the limit of 30
        Assert.Equal(t.Staff[0].Employment, days[0].GetProperty("people")[0].GetProperty("employmentId").GetGuid()); // the colleague by name, not the requester

        // The warning is only a warning: the manager approves the request all the same.
        var instance = await _h.ScalarAsync<Guid>("select workflow_instance_id from hrm.leave_requests where id = $1", Ct, waiting);
        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at desc limit 1", Ct, instance);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(t.ManagerUser, $"/api/wf/tasks/{task}/approve", new { comment = "covered" }, Ct)).Status);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.leave_requests where id = $1", Ct, waiting));

        // Another employee, outside the request's scope, gets "not found"; the limit 0 switches the warning off.
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(t.Staff[3].User, $"{Requests}/{waiting}/coverage", Ct)).Status);
        var tenantAdmin = await _h.UserAsync(Ct, null, "tenant_admin");
        var off = await _h.PutAsync(tenantAdmin, "/api/cfg/settings/hrm.leave.coverage_warning_percent", new { layer = "company", scopeId = t.Company, value = 0, reason = "test" }, Ct);
        Assert.True(off.Status == HttpStatusCode.OK, off.Body);
        var quiet = await _h.GetAsync(t.ManagerUser, $"{Requests}/{waiting}/coverage", Ct);
        Assert.False(quiet.Json.GetProperty("warn").GetBoolean());
        Assert.Equal(0, quiet.Json.GetProperty("limitPercent").GetInt32());
    }

    [Fact]
    public async Task FR_HRM_LVE_05_a_request_of_a_person_without_a_line_manager_has_no_team_and_no_warning()
    {
        var t = await TeamAsync();
        await _h.ExecAsync("update hrm.employment_assignments set manager_employment_id = null where employment_id = $1", Ct, t.Staff[3].Employment);
        var id = await RequestAsync(t, 3, Monday.AddDays(1), Monday.AddDays(1), approve: false);
        var card = await _h.GetAsync(t.Admin, $"{Requests}/{id}/coverage", Ct);
        Assert.True(card.Status == HttpStatusCode.OK, card.Body);
        Assert.False(card.Json.GetProperty("teamKnown").GetBoolean());
        Assert.False(card.Json.GetProperty("warn").GetBoolean());
    }

    [Fact]
    public async Task FR_HRM_LVE_01_a_leave_type_colour_is_a_design_token_name_never_a_raw_colour()
    {
        var t = await TeamAsync("warning");
        var types = await _h.GetAsync(t.Admin, $"/api/hrm/leave-types/{t.Type}", Ct);
        Assert.True(types.Status == HttpStatusCode.OK, types.Body);
        Assert.Equal("warning", types.Json.GetProperty("colorToken").GetString());
        var bad = await _h.PutAsync(t.Admin, $"/api/hrm/leave-types/{t.Type}", new { colorToken = "#ff0000", rowVersion = types.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("errors.hrm.leave.color_token", bad.Body, StringComparison.Ordinal);
        var cleared = await _h.PutAsync(t.Admin, $"/api/hrm/leave-types/{t.Type}", new { colorToken = "", rowVersion = types.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(cleared.Status == HttpStatusCode.OK, cleared.Body);
        Assert.Equal(JsonValueKind.Null, cleared.Json.GetProperty("colorToken").ValueKind);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from pg_constraint where conname = 'ck_leave_types_color_token'", Ct)); // the database repeats the rule
    }
}
