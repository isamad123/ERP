#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// The live attendance dashboard of slice S1.30b (FR-HRM-ATT-18, FR-HRM-SSP-02, FR-HRM-RPT-05 for the scope) through the real API pipeline, the real access
/// engine and a real PostgreSQL. Shifts are anchored to the company's midnight so the answer does not depend on the time of day the test runs (the test
/// keeps away from the minutes around midnight): a shift from 00:00 to 23:59 has started, one from 00:00 to 00:01 has ended, one from 23:50 to 23:59 is not due.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceDashboardTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Dashboard = "/api/hrm/attendance/dashboard";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        ReportOrg.AvoidMidnight(_h);
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record People(Guid Present, Guid Late, Guid NotYet, Guid Leave, Guid Duty, Guid Off, Guid NotDue, Guid Absent, Guid Manager, Guid ManagerUser, Guid PresentUser);

    private async Task<(ReportOrg Org, People P)> SetUpAsync()
    {
        var o = await ReportOrg.CreateAsync(_h);
        var started = await o.ShiftAsync("00:00", "23:59");
        var ended = await o.ShiftAsync("00:00", "00:01");
        var later = await o.ShiftAsync("23:50", "23:59");
        var today = o.Today;
        var (manager, managerUser) = await o.HireAsync("line_manager");
        var (present, presentUser) = await o.HireAsync(manager: manager);
        var (late, _) = await o.HireAsync(manager: manager);
        var (notYet, _) = await o.HireAsync();
        var (leave, _) = await o.HireAsync();
        var (duty, _) = await o.HireAsync();
        var (off, _) = await o.HireAsync();
        var (notDue, _) = await o.HireAsync();
        var (absent, _) = await o.HireAsync();
        foreach (var e in new[] { present, late, notYet, leave, duty })
        {
            await o.RosterAsync(e, started, today);
        }

        await o.RosterAsync(notDue, later, today);
        await o.RosterAsync(absent, ended, today);

        // present: punched at 00:02 (inside the grace of 10 minutes); late: punched a minute ago, after start plus grace.
        await o.PunchAsync(present, (ReportOrg.At(today, "00:02"), "in"));
        var oneMinuteAgo = _h.Clock.UtcNow.AddMinutes(-1);
        await o.PunchAsync(late, (oneMinuteAgo, "in"));
        await _h.RowAsync("hrm.leave_request_days", o.Company, new Dictionary<string, object?> { ["employment_id"] = leave, ["leave_date"] = today.ToDateTime(TimeOnly.MinValue), ["request_status"] = "approved", ["company_id"] = o.Company }, Ct);
        await _h.RowAsync("hrm.duty_requests", o.Company, new Dictionary<string, object?>
        {
            ["employment_id"] = duty, ["kind"] = "field_duty", ["status"] = "approved", ["company_id"] = o.Company, ["start_at"] = ReportOrg.At(today, "00:00").ToUniversalTime(), ["end_at"] = ReportOrg.At(today.AddDays(1), "00:00").ToUniversalTime(),
        }, Ct);
        return (o, new People(present, late, notYet, leave, duty, off, notDue, absent, manager, managerUser, presentUser));
    }

    private static int N(JsonElement counts, string name) => counts.GetProperty(name).GetInt32();

    private async Task<JsonElement> CountsAsync(Guid user, ReportOrg o, string query = "")
    {
        var answer = await _h.GetAsync(user, $"{Dashboard}?companyId={o.Company}{query}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json.GetProperty("totals").Clone();
    }

    private async Task<List<string>> ListAsync(Guid user, ReportOrg o, string status)
    {
        var answer = await _h.GetAsync(user, $"{Dashboard}/people?companyId={o.Company}&status={status}&limit=500", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return [.. answer.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("employmentId").GetGuid().ToString())];
    }

    [Fact]
    public async Task S130b_FR_HRM_ATT_18_the_dashboard_counts_present_late_absent_on_leave_on_duty_not_yet_punched_and_not_due_and_every_count_equals_its_list()
    {
        var (o, p) = await SetUpAsync();
        var totals = await CountsAsync(o.Hr, o);

        Assert.Equal(9, N(totals, "headcount")); // eight people with a place and the manager (no shift today)
        Assert.Equal(2, N(totals, "present")); // punched in: the on-time one and the late one (late people are present)
        Assert.Equal(1, N(totals, "late"));
        Assert.Equal(1, N(totals, "notYetPunched"));
        Assert.Equal(1, N(totals, "onLeave"));
        Assert.Equal(1, N(totals, "onDuty"));
        Assert.Equal(1, N(totals, "notDue"));
        Assert.Equal(1, N(totals, "absent"));
        Assert.Equal(2, N(totals, "notScheduled")); // the manager and the person with no roster
        Assert.Equal(0, N(totals, "noPolicy"));
        Assert.Equal(7, N(totals, "scheduled"));

        // Dashboard counts equal list counts, status by status (ATT-18).
        Assert.Equal([p.Present.ToString(), p.Late.ToString()], (await ListAsync(o.Hr, o, "present")).Order(StringComparer.Ordinal));
        Assert.Equal([p.Late.ToString()], await ListAsync(o.Hr, o, "late"));
        Assert.Equal([p.NotYet.ToString()], await ListAsync(o.Hr, o, "not_yet_punched"));
        Assert.Equal([p.Leave.ToString()], await ListAsync(o.Hr, o, "on_leave"));
        Assert.Equal([p.Duty.ToString()], await ListAsync(o.Hr, o, "on_duty"));
        Assert.Equal([p.NotDue.ToString()], await ListAsync(o.Hr, o, "not_due"));
        Assert.Equal([p.Absent.ToString()], await ListAsync(o.Hr, o, "absent"));
        Assert.Equal(2, (await ListAsync(o.Hr, o, "not_scheduled")).Count);
        Assert.Equal(9, (await _h.GetAsync(o.Hr, $"{Dashboard}/people?companyId={o.Company}&limit=500", Ct)).Json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task S130b_FR_HRM_ATT_18_the_counts_are_also_given_by_unit_and_location_and_filter_by_them_and_the_refresh_interval_is_the_setting()
    {
        var (o, _) = await SetUpAsync();
        var all = await _h.GetAsync(o.Hr, $"{Dashboard}?companyId={o.Company}", Ct);
        var unit = all.Json.GetProperty("byUnit").EnumerateArray().Single();
        Assert.Equal(o.Place.OrgUnit, unit.GetProperty("unitId").GetGuid());
        Assert.Equal(9, unit.GetProperty("counts").GetProperty("headcount").GetInt32());
        Assert.Equal(60, all.Json.GetProperty("refreshSeconds").GetInt32()); // hrm.att.dashboard_refresh_seconds default
        Assert.Equal(0, all.Json.GetProperty("byLocation").GetArrayLength()); // nobody was hired into a location

        // A unit with nobody in it shows nothing.
        var otherUnit = (Guid)(await _h.RowAsync("org.org_units", o.Company, null, Ct))["id"]!;
        Assert.Equal(0, N(await CountsAsync(o.Hr, o, $"&unitId={otherUnit}"), "headcount"));
        Assert.Equal(9, N(await CountsAsync(o.Hr, o, $"&unitId={o.Place.OrgUnit}"), "headcount"));

        await _h.ExecAsync("insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by) values (gen_random_uuid(), 'hrm.att.dashboard_refresh_seconds', 'tenant', null, '30'::jsonb, now(), $1, now(), $1)",
            Ct, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        Assert.Equal(30, (await _h.GetAsync(o.Hr, $"{Dashboard}?companyId={o.Company}", Ct)).Json.GetProperty("refreshSeconds").GetInt32());
    }

    [Fact]
    public async Task S130b_FR_HRM_SSP_02_FR_HRM_RPT_05_a_manager_sees_only_the_team_an_employee_has_no_dashboard_and_another_company_sees_nothing()
    {
        var (o, p) = await SetUpAsync();

        var team = await CountsAsync(p.ManagerUser, o);
        Assert.Equal(2, N(team, "headcount")); // the two people who report to the manager
        Assert.Equal(2, N(team, "present"));
        Assert.Equal(1, N(team, "late"));
        Assert.Equal(0, N(team, "absent"));
        Assert.Equal(2, (await ListAsync(p.ManagerUser, o, "present")).Count);
        Assert.Empty(await ListAsync(p.ManagerUser, o, "absent"));

        // An employee (own scope, no dashboard permission) is refused; the same person sees nothing of others anywhere.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(p.PresentUser, Dashboard, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(p.PresentUser, $"{Dashboard}/people", Ct)).Status);

        // A person who may see the dashboard for another company only sees no one of this company.
        var other = await _h.CompanyAsync(Ct);
        var viewer = await _h.UserAsync(Ct);
        await _h.AssignAsync(viewer, await _h.RoleAsync(Ct, ["hrm.attendance_dashboard.read", "hrm.attendance_days.read", "hrm.employments.read"], [("*", "all")]), other, _h.Today.AddDays(-1), null, Ct);
        var seen = await _h.GetAsync(viewer, $"{Dashboard}?companyId={o.Company}", Ct);
        Assert.Equal(HttpStatusCode.OK, seen.Status);
        Assert.Equal(0, seen.Json.GetProperty("totals").GetProperty("headcount").GetInt32());
    }

    [Fact]
    public async Task S130b_FR_HRM_ATT_18_without_an_attendance_policy_lateness_is_not_judged_and_nobody_is_called_late_or_absent()
    {
        var o = await ReportOrg.CreateAsync(_h, withPolicy: false);
        var shift = await o.ShiftAsync("00:00", "23:59");
        var (silent, _) = await o.HireAsync();
        var (punched, _) = await o.HireAsync();
        await o.RosterAsync(silent, shift, o.Today);
        await o.RosterAsync(punched, shift, o.Today);
        await o.PunchAsync(punched, (_h.Clock.UtcNow.AddMinutes(-1), "in"));
        var totals = await CountsAsync(o.Hr, o);
        Assert.Equal(1, N(totals, "noPolicy")); // a shift today, no punch, no policy: no number has a default (AS-4)
        Assert.Equal(1, N(totals, "present"));
        Assert.Equal(0, N(totals, "late"));
        Assert.Equal(0, N(totals, "absent"));
        Assert.Equal(0, N(totals, "notYetPunched"));
    }
}
