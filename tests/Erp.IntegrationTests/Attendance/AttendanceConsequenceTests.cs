#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Hrm.Contracts.Attendance;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// Lateness rules, waivers, overtime and its hand-off of slice S1.29b through the real API pipeline, access engine, workflow engine, attendance engine,
/// leave ledger, event dispatcher and a real PostgreSQL: FR-HRM-ATT-13 (the fourth late of a month beyond three allowed deducts one leave unit through
/// the leave ledger; a pay deduction reaches payroll through <c>ILatenessDeductions</c>; a warning only records; a corrected day moves the penalties;
/// a waiver by someone else gives the leave back and keeps the record as waived), FR-HRM-ATT-12 (overtime by day type from the worked minutes, claimed
/// after the day or asked before it, daily and monthly caps enforced on approval, payable minutes through <c>IOvertimeInputs</c>), FR-HRM-LVE-08 (approved
/// overtime the policy converts earns comp-off in the leave ledger once), FR-XM-26 (a locked day refuses claims, waivers and approvals) and FR-XM-32
/// (the employee never approves their own penalty's waiver or overtime; a subject without a sign-in reaches their own line manager, R-305).
/// Dates come from the company's day in Asia/Karachi on the harness clock.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceConsequenceTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Dictionary<string, object> Base() => new()
    {
        ["lateGraceMinutes"] = 10, ["earlyGraceMinutes"] = 10, ["absentBelowMinutes"] = 240, ["breakMode"] = "fixed", ["breakMinutes"] = 60, ["roundingMode"] = "none",
        ["shiftWindowMinutes"] = 240, ["weeklyOffWork"] = "record", ["holidayWork"] = "record",
    };

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Hr, Guid Approver, Guid PayGroup, Guid Day, DateOnly Monday);

    /// <summary>
    /// A company with an HR user, an approver (baseline HR Admin), a pay group with periods, a day shift 09:00 to 18:00 and the attendance policy; Monday
    /// is a Monday at least a week back whose week up to Friday lies in one calendar month (the month of the allowance and the caps).
    /// </summary>
    private async Task<Org> OrgAsync(Dictionary<string, object> policy)
    {
        var company = await _h.CompanyAsync(Ct);
        var hr = await _h.UserAsync(Ct);
        var everyHr = await _h.ScalarAsync<string[]>("select array_agg(code order by code) from iam.permissions where code like 'hrm.%'", Ct);
        await _h.AssignAsync(hr, await _h.RoleAsync(Ct, everyHr, [("*", "all")], [("*", "class:salary", "editable")]), null, _h.Today.AddDays(-1), null, Ct);
        var approver = await _h.UserAsync(Ct, null, "hr_admin");
        var monday = Today.AddDays(-8);
        while (monday.DayOfWeek != DayOfWeek.Monday || monday.Month != monday.AddDays(4).Month)
        {
            monday = monday.AddDays(-1);
        }

        var code = "PG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var group = await _h.PostAsync(hr, "/api/hrm/pay-groups", new { companyId = company, code, nameI18n = new { en = "Group " + code, ur = "گروپ " + code }, frequency = "monthly", taxYearStartMonth = 1, cutOffDay = 25, paymentDay = 28 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var payGroup = group.Json.GetProperty("id").GetGuid();
        foreach (var year in new[] { monday.AddDays(-40).Year, Today.AddDays(20).Year }.Distinct())
        {
            var generated = await _h.PostAsync(hr, $"/api/hrm/pay-groups/{payGroup}/periods/generate", new { taxYear = year }, Ct);
            Assert.True(generated.Status == HttpStatusCode.Created, generated.Body);
        }

        var place = await _h.PlaceAsync(company, Ct);
        var shiftCode = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var shift = await _h.PostAsync(hr, "/api/hrm/shifts", new
        {
            companyId = company, code = shiftCode, nameI18n = new { en = "Shift " + shiftCode, ur = "شفٹ " + shiftCode }, shiftType = "fixed", startTime = "09:00", endTime = "18:00", crossesMidnight = false, minFullDayMinutes = 420,
        }, Ct);
        Assert.True(shift.Status == HttpStatusCode.Created, shift.Body);
        var org = new Org(company, place, hr, approver, payGroup, shift.Json.GetProperty("id").GetGuid(), monday);
        var from = monday.AddDays(-60);
        var (policyId, _) = await _h.ActivePolicyAsync(hr, company, "attendance", policy, from, Ct);
        var set = await _h.ActivateSetAsync(hr, company, "attendance", from, Ct, (policyId, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        return org;
    }

    /// <summary>An employment of a new user holding <paramref name="role"/> (none: no sign-in), with an optional line manager employment.</summary>
    private async Task<(Guid Employment, Guid? User)> HireAsync(Org o, string? role = "employee", Guid? manager = null)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(o.Hr, Employments, new
        {
            personId = person, companyId = o.Company, employeeType = "permanent", joiningDate = Iso(o.Monday.AddDays(-30)),
            assignment = new { orgUnitId = o.Place.OrgUnit, branchId = o.Place.Branch, title = "Officer", payGroupId = o.PayGroup, managerEmploymentId = manager },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var employment = answer.Json.GetProperty("id").GetGuid();
        if (role is null)
        {
            return (employment, null);
        }

        var user = await _h.UserAsync(Ct, null, role);
        await _h.ExecAsync("update hrm.employments set user_ref = $1 where id = $2", Ct, user, employment);
        return (employment, user);
    }

    private async Task RosterAsync(Org o, Guid employment, params (DateOnly Day, string DayType)[] days)
    {
        var created = await _h.PostAsync(o.Hr, "/api/hrm/rosters", new { companyId = o.Company, name = "Roster " + Guid.NewGuid().ToString("N")[..6], orgUnitId = o.Place.OrgUnit, periodStart = Iso(days.Min(d => d.Day)), periodEnd = Iso(days.Max(d => d.Day)) }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var roster = created.Json.GetProperty("id").GetGuid();
        var changed = await _h.PutAsync(o.Hr, $"/api/hrm/rosters/{roster}/days", new
        {
            changes = days.Select(d => new { employmentId = employment, workDate = Iso(d.Day), dayType = d.DayType, shiftId = d.DayType == "working" ? o.Day : (Guid?)null, reason = (string?)null }).ToArray(),
        }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.rosters where id = $1", Ct, roster);
        var published = await _h.PostAsync(o.Hr, $"/api/hrm/rosters/{roster}/publish", new { rowVersion = version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
    }

    private static (DateOnly, string)[] WorkingDays(params DateOnly[] days) => [.. days.Select(d => (d, "working"))];

    private static DateTimeOffset At(DateOnly d, string hhmm) =>
        new(d.ToDateTime(TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture)), TimeSpan.FromHours(5));

    private async Task PunchAsync(Org o, Guid employment, DateOnly day, string inAt, string outAt)
    {
        var answer = await _h.PostAsync(o.Hr, "/api/hrm/attendance/events", new
        {
            events = new[] { (At(day, inAt), "in"), (At(day, outAt), "out") }
                .Select(p => new { employmentId = employment, eventAt = p.Item1, direction = p.Item2, source = "hr_entry", idempotencyKey = "t:" + Guid.NewGuid().ToString("N") }).ToArray(),
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
    }

    private async Task ProcessAsync(Org o, Guid employment, DateOnly from, DateOnly to)
    {
        var answer = await _h.PostAsync(o.Hr, $"{Employments}/{employment}/attendance-days/process", new { from = Iso(from), to = Iso(to) }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
    }

    private Task<long> CountAsync(string sql, params object?[] values) => _h.ScalarAsync<long>(sql, Ct, values);

    private Task<Guid> TaskOfAsync(Answer request) =>
        _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, request.Json.GetProperty("instanceId").GetGuid());

    private Task<Guid> AssigneeOfAsync(Guid task) => _h.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, task);

    private async Task DispatchAsync(Func<Task<bool>> done)
    {
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var dispatcher = rig.Provider().GetRequiredService<EventDispatcher>();
        for (var pass = 0; pass < 40 && !await done().ConfigureAwait(false); pass++)
        {
            await dispatcher.DispatchOnceAsync(_h.Code, Ct);
        }
    }

    private async Task<T> InCustomerScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ResolvedTenant tenant;
        await using (var cp = new NpgsqlConnection(fx.Options.ConnectionString))
        {
            await cp.OpenAsync(Ct);
            tenant = (await Kernel.Infrastructure.ControlPlane.Tenancy.TenantDirectory.FindByCodeAsync(cp, _h.Code, Ct))!;
        }

        var dataSource = await _h.App.Services.GetRequiredService<ITenantDataSourceFactory>().GetAsync(tenant, Ct);
        var licence = await _h.App.Services.GetRequiredService<ILicenceResolver>().ResolveAsync(tenant, dataSource, Ct);
        await using var scope = _h.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant, TenantAccessMode.Full, dataSource, licence);
        return await action(scope.ServiceProvider);
    }

    private async Task<Guid> LeaveTypeAsync(Org o, string code, bool compOff = false, string unit = "day")
    {
        var created = await _h.PostAsync(o.Hr, "/api/hrm/leave-types", new
        {
            companyId = o.Company, code, nameI18n = new { en = "Leave " + code, ur = "رخصت " + code }, isPaid = true, counting = unit == "hour" ? "hours" : "working_days", unit,
            requiresDocument = false, isCompOff = compOff, isUnpaidDeduction = false,
        }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        return created.Json.GetProperty("id").GetGuid();
    }

    private Task<decimal> BalanceAsync(Guid employment, Guid type) =>
        _h.ScalarAsync<decimal>("select coalesce(sum(quantity), 0) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2", Ct, employment, type);

    // ------------------------------------------------------------------ FR-HRM-ATT-13 lateness and waiver

    [Fact]
    public async Task FR_HRM_ATT_13_FR_XM_32_the_fourth_late_deducts_one_leave_unit_and_a_waiver_by_someone_else_gives_it_back_and_keeps_the_record()
    {
        var policy = Base();
        policy["latePenalty"] = new { allowedPerMonth = 3, action = "leave", leaveTypeCode = "CLP", leaveUnits = 1 };
        var o = await OrgAsync(policy);
        var casual = await LeaveTypeAsync(o, "CLP");
        var (employment, user) = await HireAsync(o);
        var opening = await _h.PostAsync(o.Hr, $"{Employments}/{employment}/leave-ledger", new { leaveTypeId = casual, kind = "opening", quantity = 10, entryDate = Iso(o.Monday.AddDays(-20)), reason = "Opening" }, Ct);
        Assert.True(opening.Status == HttpStatusCode.Created, opening.Body);
        var days = Enumerable.Range(0, 5).Select(i => o.Monday.AddDays(i)).ToArray();
        await RosterAsync(o, employment, WorkingDays(days));
        foreach (var d in days[..4])
        {
            await PunchAsync(o, employment, d, "09:25", "18:30"); // late 25 minutes, beyond the grace of 10
        }

        await PunchAsync(o, employment, days[4], "09:00", "18:00");
        await ProcessAsync(o, employment, days[0], days[4]);
        Assert.Equal(4, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1 and superseded_at is null and result = 'late'", employment));

        // Only the fourth late of the month carries the penalty: one leave unit through the ledger, naming the penalty.
        var penalty = await _h.ScalarAsync<Guid>("select id from hrm.attendance_penalties where employment_id = $1", Ct, employment);
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_penalties where employment_id = $1", employment));
        Assert.Equal($"{Iso(days[3])}|late|25|4|leave|applied|hrm.att.penalty.leave",
            await _h.ScalarAsync<string>("select work_date::text || '|' || kind || '|' || minutes || '|' || occurrence || '|' || action || '|' || status || '|' || (explanation->>'key') from hrm.attendance_penalties where id = $1", Ct, penalty));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.leave_ledger l join hrm.attendance_penalties p on p.leave_entry_id = l.id where p.id = $1 and l.kind = 'usage' and l.quantity = -1 and l.source_type = 'hrm.attendance_penalty' and l.source_id = $1", penalty));
        Assert.Equal(9m, await BalanceAsync(employment, casual));

        // Processing again writes nothing new (repeatable).
        await ProcessAsync(o, employment, days[0], days[4]);
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_penalties where employment_id = $1", employment));
        Assert.Equal(9m, await BalanceAsync(employment, casual));

        // The employee asks for the waiver; they never approve it themselves (FR-XM-32).
        var asked = await _h.PostAsync(user!.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "Traffic accident on the motorway" }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(o.Hr, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "again" }, Ct)).Status); // one waiting waiver
        var task = await TaskOfAsync(asked);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(user.Value, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.Equal("applied", await _h.ScalarAsync<string>("select status from hrm.attendance_penalties where id = $1", Ct, penalty));

        var approved = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        // The record stays, waived, with the reason and the time; the leave is given back by a reversal.
        Assert.Equal("waived|Traffic accident on the motorway|true|true", await _h.ScalarAsync<string>(
            "select status || '|' || waiver_reason || '|' || (waived_at is not null) || '|' || (leave_reversal_id is not null) from hrm.attendance_penalties where id = $1", Ct, penalty));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.leave_ledger l join hrm.attendance_penalties p on p.leave_reversal_id = l.id where p.id = $1 and l.kind = 'usage_reversal' and l.quantity = 1", penalty));
        Assert.Equal(10m, await BalanceAsync(employment, casual));

        // A waived penalty is never touched again by the engine, and nothing new is applied for its day.
        await ProcessAsync(o, employment, days[0], days[4]);
        Assert.Equal("1|waived", await _h.ScalarAsync<string>("select count(*) || '|' || max(status) from hrm.attendance_penalties where employment_id = $1", Ct, employment));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(user.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "again" }, Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_ATT_13_a_pay_deduction_reaches_payroll_a_corrected_earlier_day_moves_the_penalty_and_early_leaving_gives_a_warning()
    {
        var policy = Base();
        policy["latePenalty"] = new { allowedPerMonth = 1, action = "pay_deduction" };
        policy["earlyPenalty"] = new { allowedPerMonth = 0, action = "warning" };
        var o = await OrgAsync(policy);
        var (employment, _) = await HireAsync(o);
        var days = Enumerable.Range(0, 3).Select(i => o.Monday.AddDays(i)).ToArray();
        await RosterAsync(o, employment, WorkingDays(days));
        await PunchAsync(o, employment, days[0], "09:30", "18:30");
        await PunchAsync(o, employment, days[1], "09:40", "18:30");
        await PunchAsync(o, employment, days[2], "09:15", "17:40"); // late 15 and left 20 minutes early
        await ProcessAsync(o, employment, days[0], days[2]);

        Assert.Equal($"{Iso(days[1])}:late:2:pay_deduction|{Iso(days[2])}:early:1:warning|{Iso(days[2])}:late:3:pay_deduction", await _h.ScalarAsync<string>(
            "select string_agg(work_date::text || ':' || kind || ':' || occurrence || ':' || action, '|' order by work_date, kind) from hrm.attendance_penalties where employment_id = $1 and status = 'applied'", Ct, employment));
        var deductions = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILatenessDeductions>().ForAsync([employment], days[0], days[2], Ct));
        Assert.Equal([(days[1], "late", 40, 2), (days[2], "late", 15, 3)], deductions.Select(d => (d.WorkDate, d.Kind, d.Minutes, d.Occurrence)));
        Assert.All(deductions, d => Assert.False(d.Locked));

        // The first day is corrected (an earlier in punch replaces the late one): it is no longer late, so the second late becomes the first (allowed)
        // and its penalty is cancelled (kept as the record); the third becomes the second.
        var late = await _h.ScalarAsync<Guid>("select id from hrm.attendance_events where employment_id = $1 and event_date = $2 and direction = 'in'", Ct, employment, days[0]);
        var corrected = await _h.PostAsync(o.Hr, "/api/hrm/attendance/events", new
        {
            events = new[] { new { employmentId = employment, eventAt = At(days[0], "09:00"), direction = "in", source = "hr_entry", idempotencyKey = "t:" + Guid.NewGuid().ToString("N"), supersedesEventId = late } },
        }, Ct);
        Assert.True(corrected.Status == HttpStatusCode.OK, corrected.Body);
        await ProcessAsync(o, employment, days[0], days[2]);
        Assert.Equal("present", await _h.ScalarAsync<string>("select result from hrm.attendance_days where employment_id = $1 and work_date = $2 and superseded_at is null", Ct, employment, days[0]));
        Assert.Equal($"{Iso(days[1])}:late:2:pay_deduction:cancelled|{Iso(days[2])}:early:1:warning:applied|{Iso(days[2])}:late:2:pay_deduction:applied", await _h.ScalarAsync<string>(
            "select string_agg(work_date::text || ':' || kind || ':' || occurrence || ':' || action || ':' || status, '|' order by work_date, kind) from hrm.attendance_penalties where employment_id = $1", Ct, employment));
        deductions = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILatenessDeductions>().ForAsync([employment], days[0], days[2], Ct));
        Assert.Equal([(days[2], 2)], deductions.Select(d => (d.WorkDate, d.Occurrence)));
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-12 overtime

    private static Dictionary<string, object> WorkingOvertime(string approval, int? daily, int? monthly, string compensation = "pay")
    {
        var rule = new Dictionary<string, object> { ["multiplier"] = 1.5, ["approval"] = approval, ["minimumMinutes"] = 30, ["countBeforeShift"] = false, ["compensation"] = compensation };
        if (daily is { } d)
        {
            rule["dailyCapMinutes"] = d;
        }

        if (monthly is { } m)
        {
            rule["monthlyCapMinutes"] = m;
        }

        return rule;
    }

    [Fact]
    public async Task FR_HRM_ATT_12_FR_XM_32_worked_overtime_is_claimed_and_approved_within_the_daily_and_monthly_caps_and_reaches_payroll()
    {
        var policy = Base();
        policy["overtime"] = new { working = WorkingOvertime("post", 60, 90) };
        var o = await OrgAsync(policy);
        var (employment, user) = await HireAsync(o);
        var days = Enumerable.Range(0, 4).Select(i => o.Monday.AddDays(i)).ToArray();
        await RosterAsync(o, employment, WorkingDays(days));
        await PunchAsync(o, employment, days[0], "09:00", "20:00"); // 120 beyond the shift
        await PunchAsync(o, employment, days[1], "09:00", "19:30"); // 90
        await PunchAsync(o, employment, days[2], "09:00", "19:00"); // 60
        await PunchAsync(o, employment, days[3], "09:00", "18:20"); // 20: below the minimum of 30
        await ProcessAsync(o, employment, days[0], days[3]);

        Assert.Equal("120|90|60|0", await _h.ScalarAsync<string>("select string_agg(overtime_minutes::text, '|' order by work_date) from hrm.attendance_days where employment_id = $1 and superseded_at is null", Ct, employment));
        Assert.Equal("120:calculated:1.5|90:calculated:1.5|60:calculated:1.5", await _h.ScalarAsync<string>(
            "select string_agg(minutes || ':' || status || ':' || trim_scale(rate_multiplier)::text, '|' order by work_date) from hrm.overtime_entries where employment_id = $1", Ct, employment));
        var entries = (await _h.ScalarAsync<Guid[]>("select array_agg(id order by work_date) from hrm.overtime_entries where employment_id = $1", Ct, employment)).ToList();

        // Day 1 claimed by the employee: approved at the daily cap of 60; the employee cannot approve it.
        var claim = await _h.PostAsync(user!.Value, $"/api/hrm/attendance/overtime-entries/{entries[0]}/claim", new { reason = "Month-end close" }, Ct);
        Assert.True(claim.Status == HttpStatusCode.Accepted, claim.Body);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(user.Value, $"/api/hrm/attendance/overtime-entries/{entries[0]}/claim", new { reason = "again" }, Ct)).Status);
        var task = await TaskOfAsync(claim);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(user.Value, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);

        // Day 2: only 30 are left of the monthly cap of 90.
        var second = await _h.PostAsync(user.Value, $"/api/hrm/attendance/overtime-entries/{entries[1]}/claim", new { reason = "Audit" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(second)}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);

        // Day 3: the monthly cap is used up, so the approval is refused and the approver rejects.
        var third = await _h.PostAsync(o.Hr, $"/api/hrm/attendance/overtime-entries/{entries[2]}/claim", new { reason = "Stock count" }, Ct);
        Assert.True(third.Status == HttpStatusCode.Accepted, third.Body);
        Assert.True(third.Json.GetProperty("onBehalf").GetBoolean());
        var thirdTask = await TaskOfAsync(third);
        var refused = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{thirdTask}/approve", new { comment = "ok" }, Ct);
        Assert.NotEqual(HttpStatusCode.OK, refused.Status);
        Assert.Contains("errors.hrm.attendance.overtime.cap_reached", refused.Body, StringComparison.Ordinal);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{thirdTask}/reject", new { comment = "cap" }, Ct)).Status == HttpStatusCode.OK);

        Assert.Equal("60:approved|30:approved|:rejected", await _h.ScalarAsync<string>(
            "select string_agg(coalesce(approved_minutes::text, '') || ':' || status, '|' order by work_date) from hrm.overtime_entries where employment_id = $1", Ct, employment));
        var inputs = await InCustomerScopeAsync(sp => sp.GetRequiredService<IOvertimeInputs>().ForAsync([employment], days[0], days[3], Ct));
        Assert.Equal([(days[0], 60, 1.5m), (days[1], 30, 1.5m)], inputs.Select(i => (i.WorkDate, i.Minutes, i.Multiplier)));
        Assert.All(inputs, i => Assert.True(i.PeriodId is not null && !i.Locked && i.DayType == "working"));

        // A rejected claim is not raised again when the day is processed again.
        await ProcessAsync(o, employment, days[0], days[3]);
        Assert.Equal(3, await CountAsync("select count(*) from hrm.overtime_entries where employment_id = $1", employment));
    }

    [Fact]
    public async Task FR_HRM_ATT_12_overtime_asked_before_the_day_is_approved_within_the_cap_and_pays_no_more_than_was_worked()
    {
        var policy = Base();
        policy["overtime"] = new { working = WorkingOvertime("pre", 60, null) };
        var o = await OrgAsync(policy);
        var (employment, user) = await HireAsync(o);
        var day = Today.AddDays(2);
        await RosterAsync(o, employment, WorkingDays(o.Monday, day));

        // Before the day only: a past day is refused; worked overtime is not claimed afterwards under pre-approval.
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(user!.Value, "/api/hrm/attendance/overtime-requests", new { workDate = Iso(o.Monday), minutes = 60, reason = "Late" }, Ct)).Status);
        await PunchAsync(o, employment, o.Monday, "09:00", "20:00");
        await ProcessAsync(o, employment, o.Monday, o.Monday);
        Assert.Equal(120, await _h.ScalarAsync<int>("select overtime_minutes from hrm.attendance_days where employment_id = $1 and work_date = $2 and superseded_at is null", Ct, employment, o.Monday));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.overtime_entries where employment_id = $1 and work_date = $2", employment, o.Monday));

        var asked = await _h.PostAsync(user.Value, "/api/hrm/attendance/overtime-requests", new { workDate = Iso(day), minutes = 90, reason = "Release night" }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(user.Value, "/api/hrm/attendance/overtime-requests", new { workDate = Iso(day), minutes = 30, reason = "Again" }, Ct)).Status);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(asked)}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal("90:60:0:approved", await _h.ScalarAsync<string>(
            "select requested_minutes || ':' || approved_minutes || ':' || minutes || ':' || status from hrm.overtime_entries where employment_id = $1 and work_date = $2", Ct, employment, day));
        // Nothing worked yet, nothing payable.
        Assert.Empty(await InCustomerScopeAsync(sp => sp.GetRequiredService<IOvertimeInputs>().ForAsync([employment], day, day, Ct)));

        // Three days later the day has been worked (75 minutes beyond the shift) and processed by the nightly job: 60 approved, so 60 are payable.
        var later = new TestClock();
        later.Advance(TimeSpan.FromDays(3));
        await using (var c = await _h.OwnerAsync(null, Ct))
        {
            foreach (var (at, direction) in new[] { (At(day, "09:00"), "in"), (At(day, "19:15"), "out") })
            {
                await TenancyFixture.ExecAsync(c, """
                    insert into hrm.attendance_events (id, company_id, employment_id, event_date, event_at, direction, source, received_at, idempotency_key, is_duplicate, created_at, created_by)
                    select $1, company_id, id, $2, $3, $4, 'hr_entry', $3, $5, false, $3, $6 from hrm.employments where id = $7
                    """, Ct, Guid.CreateVersion7(), day, at.ToUniversalTime(), direction, "t:" + Guid.NewGuid().ToString("N"), BaselineSeed.SystemUserId, employment);
            }
        }

        await using (var rig = await JobRig.ForExistingAsync(fx, _h.Code, later, Ct))
        {
            var worker = rig.Worker();
            var run = await rig.QueueAsync("hrm.attendance_process", new { employment_id = employment, from = Iso(day), to = Iso(day) }, ct: Ct);
            await rig.RunUntilIdleAsync(worker, Ct);
            Assert.True(await rig.StatusAsync(run, Ct) == "succeeded", string.Join("\n", await rig.LogsAsync(run, Ct)));
        }

        Assert.Equal("75:60:approved", await _h.ScalarAsync<string>("select minutes || ':' || approved_minutes || ':' || status from hrm.overtime_entries where employment_id = $1 and work_date = $2", Ct, employment, day));
        var inputs = await InCustomerScopeAsync(sp => sp.GetRequiredService<IOvertimeInputs>().ForAsync([employment], day, day, Ct));
        Assert.Equal((60, 1.5m), (Assert.Single(inputs).Minutes, inputs[0].Multiplier));
    }

    [Fact]
    public async Task FR_HRM_LVE_08_approved_work_on_a_weekly_off_converts_to_comp_off_earned_once_in_the_leave_ledger()
    {
        var policy = Base();
        policy["overtime"] = new { weeklyOff = new { multiplier = 2, approval = "post", compensation = "comp_off", compOffDayMinutes = 420 } };
        var o = await OrgAsync(policy);
        var compOff = await LeaveTypeAsync(o, "CO" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), compOff: true, unit: "half_day");
        var (employment, user) = await HireAsync(o);
        var saturday = o.Monday.AddDays(5);
        await RosterAsync(o, employment, (saturday, "weekly_off"));
        await PunchAsync(o, employment, saturday, "09:00", "17:00"); // 420 worked after the fixed break
        await ProcessAsync(o, employment, saturday, saturday);
        var entry = await _h.ScalarAsync<Guid>("select id from hrm.overtime_entries where employment_id = $1 and work_date = $2 and day_type = 'weekly_off' and minutes = 420 and status = 'calculated'", Ct, employment, saturday);

        var claim = await _h.PostAsync(user!.Value, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "Inventory weekend" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(claim)}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal("converted_to_comp_off:420", await _h.ScalarAsync<string>("select status || ':' || approved_minutes from hrm.overtime_entries where id = $1", Ct, entry));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.comp_off_earned' and payload->>'sourceId' = $1", entry.ToString()));
        // Converted overtime is not paid.
        Assert.Empty(await InCustomerScopeAsync(sp => sp.GetRequiredService<IOvertimeInputs>().ForAsync([employment], saturday, saturday, Ct)));

        // The leave engine earns one day once, and the overtime entry names the earning.
        await DispatchAsync(async () => await CountAsync("select count(*) from hrm.overtime_entries where id = $1 and comp_off_entry_id is not null", entry) == 1);
        Assert.Equal(1, await CountAsync("select count(*) from hrm.leave_ledger l join hrm.overtime_entries o on o.comp_off_entry_id = l.id where o.id = $1 and l.kind = 'comp_off_earned' and l.quantity = 1 and l.leave_type_id = $2", entry, compOff));
        Assert.Equal(1m, await BalanceAsync(employment, compOff));
        await ProcessAsync(o, employment, saturday, saturday);
        Assert.Equal(1, await CountAsync("select count(*) from hrm.overtime_entries where employment_id = $1", employment));
    }

    // ------------------------------------------------------------------ R-305, FR-XM-26, permissions

    [Fact]
    public async Task R_305_FR_XM_32_a_waiver_for_an_employee_without_a_sign_in_goes_to_their_own_line_manager_not_to_the_manager_of_the_HR_user()
    {
        var policy = Base();
        policy["latePenalty"] = new { allowedPerMonth = 0, action = "warning" };
        var o = await OrgAsync(policy);
        var (managerEmployment, managerUser) = await HireAsync(o, "line_manager");
        var (employment, user) = await HireAsync(o, role: null, manager: managerEmployment);
        Assert.Null(user);
        await RosterAsync(o, employment, WorkingDays(o.Monday));
        await PunchAsync(o, employment, o.Monday, "09:30", "18:30");
        await ProcessAsync(o, employment, o.Monday, o.Monday);
        var penalty = await _h.ScalarAsync<Guid>("select id from hrm.attendance_penalties where employment_id = $1 and action = 'warning' and status = 'applied'", Ct, employment);

        var asked = await _h.PostAsync(o.Hr, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "Approved late arrival" }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        Assert.True(asked.Json.GetProperty("onBehalf").GetBoolean());
        var task = await TaskOfAsync(asked);
        Assert.Equal(managerUser!.Value, await AssigneeOfAsync(task));
        Assert.True((await _h.PostAsync(managerUser.Value, $"/api/wf/tasks/{task}/approve", new { comment = "fine" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal("waived", await _h.ScalarAsync<string>("select status from hrm.attendance_penalties where id = $1", Ct, penalty));
    }

    [Fact]
    public async Task FR_XM_26_a_locked_day_refuses_claims_and_waivers_and_an_approval_waiting_since_before_the_lock()
    {
        var policy = Base();
        policy["latePenalty"] = new { allowedPerMonth = 0, action = "warning" };
        policy["overtime"] = new { working = WorkingOvertime("post", null, null) };
        var o = await OrgAsync(policy);
        var (employment, user) = await HireAsync(o);
        var days = new[] { o.Monday, o.Monday.AddDays(1) };
        await RosterAsync(o, employment, WorkingDays(days));
        await PunchAsync(o, employment, days[0], "09:30", "20:00");
        await PunchAsync(o, employment, days[1], "09:00", "20:00");
        await ProcessAsync(o, employment, days[0], days[1]);
        var penalty = await _h.ScalarAsync<Guid>("select id from hrm.attendance_penalties where employment_id = $1", Ct, employment);
        var entries = (await _h.ScalarAsync<Guid[]>("select array_agg(id order by work_date) from hrm.overtime_entries where employment_id = $1", Ct, employment)).ToList();
        var waiting = await _h.PostAsync(user!.Value, $"/api/hrm/attendance/overtime-entries/{entries[1]}/claim", new { reason = "Before the lock" }, Ct);
        Assert.True(waiting.Status == HttpStatusCode.Accepted, waiting.Body);

        await _h.ExecAsync("update hrm.attendance_periods set status = 'locked' where pay_group_id = $1", Ct, o.PayGroup);
        var claim = await _h.PostAsync(user.Value, $"/api/hrm/attendance/overtime-entries/{entries[0]}/claim", new { reason = "Late" }, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.request.period_locked"), (claim.Status, claim.Text("title")));
        var waiver = await _h.PostAsync(user.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "Late" }, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.request.period_locked"), (waiver.Status, waiver.Text("title")));
        var approve = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(waiting)}/approve", new { comment = "ok" }, Ct);
        Assert.NotEqual(HttpStatusCode.OK, approve.Status);
        Assert.Contains("errors.hrm.attendance.request.period_locked", approve.Body, StringComparison.Ordinal);
        var inputs = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILatenessDeductions>().ForAsync([employment], days[0], days[1], Ct));
        Assert.Empty(inputs); // a warning is no pay deduction
    }

    [Fact]
    public async Task FR_HRM_ATT_12_13_permission_scope_closed_writes_and_the_database_rules()
    {
        var policy = Base();
        policy["latePenalty"] = new { allowedPerMonth = 0, action = "warning" };
        policy["overtime"] = new { working = WorkingOvertime("post", null, null) };
        var o = await OrgAsync(policy);
        var (employment, user) = await HireAsync(o);
        var (_, other) = await HireAsync(o);
        await RosterAsync(o, employment, WorkingDays(o.Monday));
        await PunchAsync(o, employment, o.Monday, "09:30", "20:00");
        await ProcessAsync(o, employment, o.Monday, o.Monday);
        var penalty = await _h.ScalarAsync<Guid>("select id from hrm.attendance_penalties where employment_id = $1", Ct, employment);
        var entry = await _h.ScalarAsync<Guid>("select id from hrm.overtime_entries where employment_id = $1", Ct, employment);

        // Another employee (scope own) does not find them; someone without the permission is refused; the generated writes are closed.
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(other!.Value, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(other.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "x" }, Ct)).Status);
        var nobody = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, "/api/hrm/attendance/overtime-requests", new { employmentId = employment, workDate = Iso(Today), minutes = 30, reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(user!.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = " " }, Ct)).Status);
        var notPre = await _h.PostAsync(user.Value, "/api/hrm/attendance/overtime-requests", new { workDate = Iso(Today.AddDays(1)), minutes = 30, reason = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notPre.Status); // the policy has working-day overtime claimed afterwards, not asked before
        Assert.Contains("errors.hrm.attendance.overtime.not_pre_approval", notPre.Body, StringComparison.Ordinal);
        Assert.True((await _h.GetAsync(user.Value, $"/api/hrm/attendance-penalties?employmentId={employment}", Ct)).Status == HttpStatusCode.OK);
        Assert.True((await _h.GetAsync(user.Value, $"/api/hrm/overtime-entries?employmentId={employment}", Ct)).Status == HttpStatusCode.OK);
        foreach (var path in new[] { "/api/hrm/attendance-penalties", "/api/hrm/overtime-entries" })
        {
            var write = await _h.PostAsync(o.Hr, path, new { companyId = o.Company, employmentId = employment }, Ct);
            Assert.True(write.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{path}: {write.Status}");
        }

        // The database keeps the record: a penalty asked about is never deleted and its facts never change; a claimed overtime entry is never removed.
        Assert.True((await _h.PostAsync(user.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "Doctor visit" }, Ct)).Status == HttpStatusCode.Accepted);
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("delete from hrm.attendance_penalties where id = $1", Ct, penalty)); // asked about: kept for good
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.attendance_penalties set action = 'leave', leave_quantity = 1 where id = $1", Ct, penalty));
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.overtime_entries set approved_minutes = 30 where id = $1", Ct, entry));
        Assert.True((await _h.PostAsync(user.Value, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "Evening" }, Ct)).Status == HttpStatusCode.Accepted);
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("delete from hrm.overtime_entries where id = $1", Ct, entry));
    }

    // ------------------------------------------------------------------ review S1.29b

    private async Task<(Org O, Guid Employment, Guid User, Guid Entry)> ClaimableAsync(Dictionary<string, object> overtime, string inAt = "09:00", string outAt = "20:00")
    {
        var policy = Base();
        policy["overtime"] = new { working = overtime };
        var o = await OrgAsync(policy);
        var (employment, user) = await HireAsync(o);
        await RosterAsync(o, employment, WorkingDays(o.Monday));
        await PunchAsync(o, employment, o.Monday, inAt, outAt);
        await ProcessAsync(o, employment, o.Monday, o.Monday);
        return (o, employment, user!.Value, await _h.ScalarAsync<Guid>("select id from hrm.overtime_entries where employment_id = $1", Ct, employment));
    }

    [Fact]
    public async Task review_1_FR_HRM_ATT_12_13_a_returned_claim_or_waiver_is_sent_again_or_withdrawn_and_claimed_anew()
    {
        var (o, employment, user, entry) = await ClaimableAsync(WorkingOvertime("post", null, null));
        var claim = await _h.PostAsync(user, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "Close" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(claim)}/return", new { comment = "Add the job number" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal("submitted", await _h.ScalarAsync<string>("select status from hrm.overtime_entries where id = $1", Ct, entry)); // still open, not lost

        // Sent again with the detail, then approved.
        var again = await _h.PostAsync(user, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "Close, job 4711" }, Ct);
        Assert.True(again.Status == HttpStatusCode.Accepted, again.Body);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(user, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "third" }, Ct)).Status); // waiting again
        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at desc limit 1", Ct, claim.Json.GetProperty("instanceId").GetGuid());
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal("approved|Close, job 4711", await _h.ScalarAsync<string>("select status || '|' || reason from hrm.overtime_entries where id = $1", Ct, entry));

        // Another day: returned, then withdrawn by the employee: back to worked out, and claimed anew.
        var day = o.Monday.AddDays(1);
        await RosterAsync(o, employment, WorkingDays(day));
        await PunchAsync(o, employment, day, "09:00", "19:30");
        await ProcessAsync(o, employment, day, day);
        var second = await _h.ScalarAsync<Guid>("select id from hrm.overtime_entries where employment_id = $1 and work_date = $2", Ct, employment, day);
        var first = await _h.PostAsync(user, $"/api/hrm/attendance/overtime-entries/{second}/claim", new { reason = "Audit" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(first)}/return", new { comment = "Which audit?" }, Ct)).Status == HttpStatusCode.OK);
        var withdrawn = await _h.PostAsync(user, $"/api/wf/instances/{first.Json.GetProperty("instanceId").GetGuid()}/withdraw", new { reason = "Will ask again" }, Ct);
        Assert.True(withdrawn.Status == HttpStatusCode.OK, withdrawn.Body);
        Assert.Equal("calculated", await _h.ScalarAsync<string>("select status from hrm.overtime_entries where id = $1 and workflow_instance_id is null", Ct, second));
        var anew = await _h.PostAsync(user, $"/api/hrm/attendance/overtime-entries/{second}/claim", new { reason = "Year-end audit" }, Ct);
        Assert.True(anew.Status == HttpStatusCode.Accepted, anew.Body);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(anew)}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal(2, (await InCustomerScopeAsync(sp => sp.GetRequiredService<IOvertimeInputs>().ForAsync([employment], o.Monday, day, Ct))).Count);
    }

    [Fact]
    public async Task review_1_FR_HRM_ATT_13_a_returned_waiver_is_sent_again_and_approved()
    {
        var policy = Base();
        policy["latePenalty"] = new { allowedPerMonth = 0, action = "warning" };
        var o = await OrgAsync(policy);
        var (employment, user) = await HireAsync(o);
        await RosterAsync(o, employment, WorkingDays(o.Monday));
        await PunchAsync(o, employment, o.Monday, "09:30", "18:30");
        await ProcessAsync(o, employment, o.Monday, o.Monday);
        var penalty = await _h.ScalarAsync<Guid>("select id from hrm.attendance_penalties where employment_id = $1", Ct, employment);
        var asked = await _h.PostAsync(user!.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "Bus" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(asked)}/return", new { comment = "Proof?" }, Ct)).Status == HttpStatusCode.OK);
        var again = await _h.PostAsync(user.Value, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "Bus broke down, ticket attached" }, Ct);
        Assert.True(again.Status == HttpStatusCode.Accepted, again.Body);
        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at desc limit 1", Ct, asked.Json.GetProperty("instanceId").GetGuid());
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal("waived|Bus broke down, ticket attached", await _h.ScalarAsync<string>("select status || '|' || waiver_reason from hrm.attendance_penalties where id = $1", Ct, penalty));
    }

    [Theory]
    [InlineData("day", "approved", 0, 300)]
    [InlineData("half_day", "converted_to_comp_off", 240, 60)]
    [InlineData("hour", "converted_to_comp_off", 300, 0)]
    public async Task review_2_FR_HRM_LVE_08_comp_off_earns_only_whole_units_of_the_leave_type_and_the_rest_is_paid(string unit, string status, int converted, int paid)
    {
        var policy = Base();
        policy["overtime"] = new { weeklyOff = new { multiplier = 2, approval = "post", compensation = "comp_off", compOffDayMinutes = 480 } };
        var o = await OrgAsync(policy);
        var compOff = await LeaveTypeAsync(o, "CO" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), compOff: true, unit: unit);
        var (employment, user) = await HireAsync(o);
        var sunday = o.Monday.AddDays(6);
        await RosterAsync(o, employment, (sunday, "weekly_off"));
        await PunchAsync(o, employment, sunday, "09:00", "15:00"); // 300 minutes after the fixed break
        await ProcessAsync(o, employment, sunday, sunday);
        var entry = await _h.ScalarAsync<Guid>("select id from hrm.overtime_entries where employment_id = $1 and minutes = 300", Ct, employment);
        var claim = await _h.PostAsync(user!.Value, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "Sunday shipment" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(claim)}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);
        Assert.Equal($"{status}:{(converted == 0 ? string.Empty : converted)}", await _h.ScalarAsync<string>("select status || ':' || coalesce(comp_off_minutes::text, '') from hrm.overtime_entries where id = $1", Ct, entry));

        // Nothing is lost: what the type cannot earn is paid at the multiplier.
        var inputs = await InCustomerScopeAsync(sp => sp.GetRequiredService<IOvertimeInputs>().ForAsync([employment], sunday, sunday, Ct));
        Assert.Equal(paid, inputs.Sum(i => i.Minutes));
        if (converted > 0)
        {
            await DispatchAsync(async () => await CountAsync("select count(*) from hrm.overtime_entries where id = $1 and comp_off_entry_id is not null", entry) == 1);
            Assert.Equal(unit == "hour" ? 5m : 0.5m, await BalanceAsync(employment, compOff));
        }
        else
        {
            Assert.Equal(0, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.comp_off_earned' and payload->>'sourceId' = $1", entry.ToString()));
        }
    }

    [Fact]
    public async Task review_3_FR_HRM_ATT_12_approved_but_unworked_pre_approval_frees_the_monthly_cap_once_its_day_is_processed()
    {
        var policy = Base();
        policy["overtime"] = new { working = WorkingOvertime("pre", null, 120) };
        var o = await OrgAsync(policy);
        var (employment, user) = await HireAsync(o);
        var first = Today.AddDays(2);
        var second = Today.AddDays(3);
        await RosterAsync(o, employment, WorkingDays(first, second));
        var a = await _h.PostAsync(user!.Value, "/api/hrm/attendance/overtime-requests", new { workDate = Iso(first), minutes = 120, reason = "Release" }, Ct);
        var b = await _h.PostAsync(user.Value, "/api/hrm/attendance/overtime-requests", new { workDate = Iso(second), minutes = 60, reason = "Release" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(a)}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);

        // While the first day is ahead its approval holds the cap.
        var held = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(b)}/approve", new { comment = "ok" }, Ct);
        Assert.Contains("errors.hrm.attendance.overtime.cap_reached", held.Body, StringComparison.Ordinal);

        // The first day is worked without overtime and processed: its approval no longer uses the cap.
        var later = new TestClock();
        later.Advance(TimeSpan.FromDays(3) + TimeSpan.FromHours(12));
        await using (var c = await _h.OwnerAsync(null, Ct))
        {
            foreach (var (at, direction) in new[] { (At(first, "09:00"), "in"), (At(first, "18:00"), "out") })
            {
                await TenancyFixture.ExecAsync(c, """
                    insert into hrm.attendance_events (id, company_id, employment_id, event_date, event_at, direction, source, received_at, idempotency_key, is_duplicate, created_at, created_by)
                    select $1, company_id, id, $2, $3, $4, 'hr_entry', $3, $5, false, $3, $6 from hrm.employments where id = $7
                    """, Ct, Guid.CreateVersion7(), first, at.ToUniversalTime(), direction, "t:" + Guid.NewGuid().ToString("N"), BaselineSeed.SystemUserId, employment);
            }
        }

        await using (var rig = await JobRig.ForExistingAsync(fx, _h.Code, later, Ct))
        {
            var worker = rig.Worker();
            var run = await rig.QueueAsync("hrm.attendance_process", new { employment_id = employment, from = Iso(first), to = Iso(first) }, ct: Ct);
            await rig.RunUntilIdleAsync(worker, Ct);
            Assert.True(await rig.StatusAsync(run, Ct) == "succeeded", string.Join("\n", await rig.LogsAsync(run, Ct)));
        }

        Assert.Equal("0:120:approved", await _h.ScalarAsync<string>("select minutes || ':' || approved_minutes || ':' || status from hrm.overtime_entries where employment_id = $1 and work_date = $2", Ct, employment, first));
        var approved = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(b)}/approve", new { comment = "ok" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("60:approved", await _h.ScalarAsync<string>("select approved_minutes || ':' || status from hrm.overtime_entries where employment_id = $1 and work_date = $2", Ct, employment, second));
    }

    [Fact]
    public async Task review_6_FR_HRM_ATT_12_13_the_database_allows_only_the_moves_the_services_make()
    {
        var (o, _, user, entry) = await ClaimableAsync(WorkingOvertime("post", null, null));
        var claim = await _h.PostAsync(user, $"/api/hrm/attendance/overtime-entries/{entry}/claim", new { reason = "Close" }, Ct);
        Assert.True((await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(claim)}/approve", new { comment = "ok" }, Ct)).Status == HttpStatusCode.OK);
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.overtime_entries set status = 'submitted' where id = $1", Ct, entry));
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.overtime_entries set status = 'calculated' where id = $1", Ct, entry));
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.overtime_entries set status = 'converted_to_comp_off', comp_off_minutes = 9999 where id = $1", Ct, entry));
    }
}
