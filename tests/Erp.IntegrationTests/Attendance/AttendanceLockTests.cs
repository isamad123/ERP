#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Leave;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Attendance;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// The attendance period lock of slice S1.31 through the real API pipeline, access engine, workflow engine, attendance engine, leave ledger, job worker and a
/// real PostgreSQL: FR-XM-26 and FR-HRM-ATT-17 (HR locks after the cut-off date with approval; the lock freezes day results, counted leave days, leave
/// usage, payable overtime and penalties at the API and in the database; a later change is an adjustment carried into the next open payroll period, never
/// an edit of the locked period), FR-HRM-ATT-21 (payroll's lock with its run and the reopening by cancelling that run before it is locked publish their
/// events with the employments), FR-XM-05 and -06 (a lock and an adjustment never change; corrections by reversal in the next period), FR-XM-32 (the
/// requester never approves), FR-HRM-LVE-07 (locked unpaid leave), GHR-04 (the impact of a backdated change before it is asked for) and R-146 (accruals
/// still post in a locked month). Dates come from the company's day in Asia/Karachi on the harness clock.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceLockTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private const string Periods = "/api/hrm/attendance/periods";
    private const string Adjustments = "/api/hrm/attendance/adjustments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private const string AttendanceAdjustmentWaiting = "errors.hrm.attendance.adjustment.waiting";

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A company with an HR user holding every HR permission (the requester), an HR administrator and a payroll manager (approvers), a monthly pay group with
    /// the periods of last year to next year (cut-off on the 25th), a day shift and the attendance policy (one late a month allowed, then a warning; overtime on
    /// working days claimed after the day). Monday is the first Monday of last month whose week up to Friday lies in that month, so its pay period is past its
    /// cut-off and the next open payroll period is this month's.
    /// </summary>
    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Hr, Guid Approver, Guid Payroll, Guid PayGroup, Guid Day, DateOnly Monday, Guid Locked, Guid Next);

    private async Task<Org> OrgAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var hr = await _h.UserAsync(Ct);
        var everyHr = await _h.ScalarAsync<string[]>("select array_agg(code order by code) from iam.permissions where code like 'hrm.%'", Ct);
        await _h.AssignAsync(hr, await _h.RoleAsync(Ct, everyHr, [("*", "all")], [("*", "class:salary", "editable")]), null, _h.Today.AddDays(-1), null, Ct);
        var approver = await _h.UserAsync(Ct, null, "hr_admin");
        var payroll = await _h.UserAsync(Ct, null, "payroll_manager");
        var first = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        var monday = first;
        while (monday.DayOfWeek != DayOfWeek.Monday || monday.Month != monday.AddDays(4).Month)
        {
            monday = monday.AddDays(1);
        }

        var code = "PG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var group = await _h.PostAsync(hr, "/api/hrm/pay-groups", new { companyId = company, code, nameI18n = new { en = "Group " + code, ur = "گروپ " + code }, frequency = "monthly", taxYearStartMonth = 1, cutOffDay = 25, paymentDay = 28 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var payGroup = group.Json.GetProperty("id").GetGuid();
        foreach (var year in new[] { first.Year - 1, first.Year, Today.Year, Today.AddMonths(1).Year }.Distinct())
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
        var policy = new Dictionary<string, object>
        {
            ["lateGraceMinutes"] = 10, ["earlyGraceMinutes"] = 10, ["absentBelowMinutes"] = 240, ["breakMode"] = "fixed", ["breakMinutes"] = 60, ["roundingMode"] = "none",
            ["shiftWindowMinutes"] = 240, ["weeklyOffWork"] = "record", ["holidayWork"] = "record",
            ["latePenalty"] = new { allowedPerMonth = 1, action = "warning" },
            ["overtime"] = new { working = new { multiplier = 1.5, approval = "post", minimumMinutes = 30, countBeforeShift = false, compensation = "pay" } },
        };
        var from = first.AddDays(-90);
        var (policyId, _) = await _h.ActivePolicyAsync(hr, company, "attendance", policy, from, Ct);
        var set = await _h.ActivateSetAsync(hr, company, "attendance", from, Ct, (policyId, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        var locked = await PayPeriodOfAsync(payGroup, monday);
        var next = await PayPeriodOfAsync(payGroup, new DateOnly(Today.Year, Today.Month, 1));
        return new Org(company, place, hr, approver, payroll, payGroup, shift.Json.GetProperty("id").GetGuid(), monday, locked, next);
    }

    private Task<Guid> PayPeriodOfAsync(Guid group, DateOnly day) =>
        _h.ScalarAsync<Guid>("select id from hrm.pay_periods where pay_group_id = $1 and start_date <= $2 and end_date >= $2", Ct, group, day);

    /// <summary>An employment in the org's pay group of a new user holding <paramref name="role"/>, joined a month before Monday.</summary>
    private async Task<(Guid Employment, Guid User)> HireAsync(Org o, string role = "employee")
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(o.Hr, Employments, new
        {
            personId = person, companyId = o.Company, employeeType = "permanent", joiningDate = Iso(o.Monday.AddDays(-30)),
            assignment = new { orgUnitId = o.Place.OrgUnit, branchId = o.Place.Branch, title = "Officer", payGroupId = o.PayGroup },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var employment = answer.Json.GetProperty("id").GetGuid();
        var user = await _h.UserAsync(Ct, null, role);
        await _h.ExecAsync("update hrm.employments set user_ref = $1 where id = $2", Ct, user, employment);
        return (employment, user);
    }

    private async Task RosterAsync(Org o, Guid employment, DateOnly[] days)
    {
        var created = await _h.PostAsync(o.Hr, "/api/hrm/rosters", new { companyId = o.Company, name = "Roster " + Guid.NewGuid().ToString("N")[..6], orgUnitId = o.Place.OrgUnit, periodStart = Iso(days.Min()), periodEnd = Iso(days.Max()) }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var roster = created.Json.GetProperty("id").GetGuid();
        var changed = await _h.PutAsync(o.Hr, $"/api/hrm/rosters/{roster}/days", new
        {
            changes = days.Select(d => new { employmentId = employment, workDate = Iso(d), dayType = "working", shiftId = (Guid?)o.Day, reason = (string?)null }).ToArray(),
        }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.rosters where id = $1", Ct, roster);
        var published = await _h.PostAsync(o.Hr, $"/api/hrm/rosters/{roster}/publish", new { rowVersion = version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
    }

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

    /// <summary>Five working days from Monday: late (and 90 minutes beyond the shift), late, on time, on time, on time; processed.</summary>
    private async Task<(Guid Employment, Guid User, DateOnly[] Days)> WeekAsync(Org o)
    {
        var (employment, user) = await HireAsync(o);
        var days = Enumerable.Range(0, 5).Select(i => o.Monday.AddDays(i)).ToArray();
        await RosterAsync(o, employment, days);
        await PunchAsync(o, employment, days[0], "09:25", "19:30");
        await PunchAsync(o, employment, days[1], "09:30", "18:00");
        foreach (var d in days[2..])
        {
            await PunchAsync(o, employment, d, "09:00", "18:00");
        }

        await ProcessAsync(o, employment, days[0], days[4]);
        return (employment, user, days);
    }

    private Task<long> CountAsync(string sql, params object?[] values) => _h.ScalarAsync<long>(sql, Ct, values);

    private Task<Guid> TaskOfAsync(Guid instance) =>
        _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, instance);

    private async Task ApproveAsync(Guid approver, Guid instance)
    {
        var approved = await _h.PostAsync(approver, $"/api/wf/tasks/{await TaskOfAsync(instance)}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    /// <summary>HR's lock of the org's locked pay period: asked by HR (accepting the open items), refused to the requester, approved by the HR administrator.</summary>
    private async Task LockByHrAsync(Org o)
    {
        var asked = await _h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/lock-request", new { reason = "Payroll of the month is being prepared", acceptOpenItems = true }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        await ApproveAsync(o.Approver, asked.Json.GetProperty("instanceId").GetGuid());
    }

    private async Task<PostgresException> RefusedAsync(string sql, params object?[] values) =>
        await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync(sql, Ct, values));

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

    /// <summary>A payroll run of the pay period (S1.35 makes them; written directly here) in the given status.</summary>
    private async Task<Guid> RunAsync(Org o, Guid payPeriod, string status)
    {
        var run = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into hrm.pay_runs (id, company_id, run_no, pay_group_id, pay_period_id, kind, currency_code, stage_log, status, created_at, created_by, updated_at, updated_by)
            select $1, company_id, $2, pay_group_id, id, 'regular', 'PKR', '[]'::jsonb, $3, now(), $4, now(), $4 from hrm.pay_periods where id = $5
            """, Ct, run, "PR-" + Guid.NewGuid().ToString("N")[..10], status, o.Hr, payPeriod);
        return run;
    }

    private Task<string> EventAsync(string type, Guid payPeriod) =>
        _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = $1 and payload->>'payPeriodId' = $2::text order by occurred_at desc limit 1", Ct, type, payPeriod);

    // ------------------------------------------------------------------ HR's lock, the freeze and late punches (FR-XM-26, FR-HRM-ATT-17, ATT-21)

    [Fact]
    public async Task FR_XM_26_FR_HRM_ATT_17_FR_XM_32_HR_locks_after_the_cut_off_with_someone_elses_approval_and_the_lock_freezes_days_overtime_and_penalties()
    {
        var o = await OrgAsync();
        var (employment, user, days) = await WeekAsync(o);
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_penalties where employment_id = $1 and status = 'applied'", employment)); // the second late of the month
        var overtime = await _h.ScalarAsync<Guid>("select id from hrm.overtime_entries where employment_id = $1", Ct, employment);
        var claim = await _h.PostAsync(user, $"/api/hrm/attendance/overtime-entries/{overtime}/claim", new { reason = "Month-end close" }, Ct);
        Assert.True(claim.Status == HttpStatusCode.Accepted, claim.Body);
        await ApproveAsync(o.Approver, claim.Json.GetProperty("instanceId").GetGuid());

        // Before the lock: the readiness item fails (no longer "not available"), the console shows what a lock would accept.
        var readiness = await _h.GetAsync(o.Hr, $"/api/hrm/pay-periods/{o.Locked}/readiness", Ct);
        Assert.True(readiness.Status == HttpStatusCode.OK, readiness.Body);
        Assert.Contains("\"code\":\"attendance_locked\",", readiness.Body.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("failed", Item(readiness.Json, "attendance_locked"));
        var detail = await _h.GetAsync(o.Hr, $"{Periods}/{o.Locked}", Ct);
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Body);
        var unprocessed = detail.Json.GetProperty("openItems").GetProperty("unprocessedDays").GetInt32();
        Assert.True(unprocessed > 0, detail.Body); // the month's other days are not processed
        Assert.True(detail.Json.GetProperty("canRequestLock").GetBoolean(), detail.Body);

        // HR may lock only after the cut-off date; days not processed are accepted only when said so; the requester never approves.
        var nextMonth = await PayPeriodOfAsync(o.PayGroup, new DateOnly(Today.Year, Today.Month, 1).AddMonths(1));
        var early = await _h.PostAsync(o.Hr, $"{Periods}/{nextMonth}/lock-request", new { reason = "Too early", acceptOpenItems = true }, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.lock.before_cut_off"), (early.Status, early.Text("title")));
        var open = await _h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/lock-request", new { reason = "Payroll of the month" }, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.lock.open_items"), (open.Status, open.Text("title")));
        var asked = await _h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/lock-request", new { reason = "Payroll of the month", acceptOpenItems = true }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var instance = asked.Json.GetProperty("instanceId").GetGuid();
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.lock.request_waiting"), await StatusTitleAsync(_h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/lock-request", new { reason = "Again", acceptOpenItems = true }, Ct)));
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Hr, $"/api/wf/tasks/{await TaskOfAsync(instance)}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.Equal("open", await _h.ScalarAsync<string>("select status from hrm.attendance_periods where pay_period_id = $1", Ct, o.Locked));
        await ApproveAsync(o.Approver, instance);

        // Locked as HR's lock: who, why and what it accepted; the event names the employment.
        Assert.Equal($"locked|manual|{o.Hr}|Payroll of the month|{unprocessed}|true", await _h.ScalarAsync<string>(
            "select status || '|' || lock_kind || '|' || locked_by || '|' || lock_reason || '|' || unprocessed_accepted || '|' || (locked_at is not null and locked_by_run_id is null) from hrm.attendance_periods where pay_period_id = $1", Ct, o.Locked));
        using (var locked = JsonDocument.Parse(await EventAsync(AttendanceLockEvents.Locked, o.Locked)))
        {
            Assert.Equal("manual", locked.RootElement.GetProperty("lockKind").GetString());
            Assert.Contains(employment, locked.RootElement.GetProperty("employmentIds").EnumerateArray().Select(e => e.GetGuid()));
        }

        Assert.Equal("passed", Item((await _h.GetAsync(o.Hr, $"/api/hrm/pay-periods/{o.Locked}/readiness", Ct)).Json, "attendance_locked"));
        Assert.True(await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().IsLockedAsync(employment, days[2], Ct)));
        Assert.False(await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().IsLockedAsync(employment, Today, Ct)));

        // At the API: a waiver, a claim and a regularization for a locked day are refused; processing again changes nothing.
        var penalty = await _h.ScalarAsync<Guid>("select id from hrm.attendance_penalties where employment_id = $1", Ct, employment);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(user, $"/api/hrm/attendance/penalties/{penalty}/waiver", new { reason = "Traffic" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(user, "/api/hrm/attendance/regularization-requests", new { workDate = Iso(days[1]), requestedIn = At(days[1], "09:00"), reason = "Late" }, Ct)).Status);
        var rows = await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", employment);
        await ProcessAsync(o, employment, days[0], days[4]);
        Assert.Equal(rows, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", employment));

        // In the database: no day result, penalty or payable overtime of the period changes; payroll may still mark approved overtime paid.
        Assert.Equal("ck_attendance_period_locked", (await RefusedAsync("update hrm.attendance_days set superseded_at = now() where employment_id = $1 and work_date = $2 and superseded_at is null", employment, days[2])).ConstraintName);
        Assert.Equal("ck_attendance_period_locked", (await RefusedAsync("""
            insert into hrm.attendance_days (id, company_id, employment_id, work_date, day_type, result, worked_minutes, late_minutes, early_minutes, overtime_minutes, explanation, period_id, processed_at,
                                             created_at, created_by, updated_at, updated_by)
            select $1, company_id, employment_id, work_date + 7, day_type, 'present', 480, 0, 0, 0, '{}'::jsonb, period_id, now(), now(), created_by, now(), created_by
            from hrm.attendance_days where employment_id = $2 and work_date = $3 and superseded_at is null
            """, Guid.CreateVersion7(), employment, days[2])).ConstraintName);
        Assert.Equal("ck_attendance_period_locked", (await RefusedAsync("update hrm.attendance_penalties set status = 'cancelled' where id = $1", penalty)).ConstraintName);
        Assert.Equal("ck_attendance_period_locked", (await RefusedAsync("update hrm.overtime_entries set approved_minutes = 30 where id = $1", overtime)).ConstraintName);
        await _h.ExecAsync("update hrm.overtime_entries set status = 'paid' where id = $1", Ct, overtime);
        Assert.Equal("ck_attendance_periods_lock", (await RefusedAsync("update hrm.attendance_periods set lock_reason = 'changed' where pay_period_id = $1", o.Locked)).ConstraintName);
        Assert.Equal("ck_attendance_periods_lock", (await RefusedAsync("update hrm.attendance_periods set status = 'open', locked_at = null, lock_kind = null, locked_by = null, unprocessed_accepted = null, reopened_at = now(), reopen_reason = 'x' where pay_period_id = $1", o.Locked)).ConstraintName);

        // A punch for a locked day after the lock is still appended (events are append-only) and listed as late; the day is not reprocessed.
        _h.Clock.Advance(TimeSpan.FromMinutes(5));
        await PunchAsync(o, employment, days[3], "08:55", "18:05");
        await ProcessAsync(o, employment, days[0], days[4]);
        Assert.Equal(rows, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", employment));
        var late = await _h.GetAsync(o.Hr, $"{Periods}/{o.Locked}/late-events", Ct);
        Assert.True(late.Status == HttpStatusCode.OK, late.Body);
        Assert.Equal(2, late.Json.GetProperty("total").GetInt32());
        Assert.All(late.Json.GetProperty("items").EnumerateArray(), e => Assert.Equal(Iso(days[3]), e.GetProperty("eventDate").GetString()));

        // An HR lock never reopens: a later change is an adjustment.
        var reopen = await _h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/reopen-request", new { reason = "Mistake" }, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.lock.use_adjustment"), (reopen.Status, reopen.Text("title")));
        var list = await _h.GetAsync(o.Hr, $"{Periods}?companyId={o.Company}&payGroupId={o.PayGroup}", Ct);
        Assert.True(list.Status == HttpStatusCode.OK, list.Body);
        var row = list.Json.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("payPeriodId").GetGuid() == o.Locked);
        Assert.Equal(("locked", "manual", "Payroll of the month"), (row.GetProperty("state").GetString(), row.GetProperty("lockKind").GetString(), row.GetProperty("lockReason").GetString()));
    }

    private static string? Item(JsonElement readiness, string code) =>
        readiness.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == code).GetProperty("status").GetString();

    private static async Task<(HttpStatusCode, string?)> StatusTitleAsync(Task<Answer> call)
    {
        var a = await call;
        return (a.Status, a.Text("title"));
    }

    // ------------------------------------------------------------------ payroll's lock and the reopening by cancelling its run (FR-HRM-ATT-21, CR-095)

    [Fact]
    public async Task FR_HRM_ATT_21_FR_XM_32_payroll_locks_with_its_run_and_cancelling_that_run_before_it_is_locked_reopens_with_the_affected_employments()
    {
        var o = await OrgAsync();
        var (employment, _, days) = await WeekAsync(o);
        var run = await RunAsync(o, o.Locked, "calculated");

        // The contract payroll's calculation calls (S1.35): refused while days are open unless accepted, then locked once.
        var refused = await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().LockAsync(new PeriodLockRequest(o.Locked, run, false, o.Hr), null, Ct));
        Assert.Equal((PeriodLockOutcome.Refused, "errors.hrm.attendance.lock.open_items"), (refused.Outcome, refused.ErrorKey));
        Assert.True(refused.UnprocessedDays > 0);
        var locked = await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().LockAsync(new PeriodLockRequest(o.Locked, run, true, o.Hr), null, Ct));
        Assert.Equal(PeriodLockOutcome.Locked, locked.Outcome);
        Assert.Equal(PeriodLockOutcome.AlreadyLocked, (await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().LockAsync(new PeriodLockRequest(o.Locked, run, true, o.Hr), null, Ct))).Outcome);
        Assert.Equal($"payroll|{run}", await _h.ScalarAsync<string>("select lock_kind || '|' || locked_by_run_id from hrm.attendance_periods where pay_period_id = $1", Ct, o.Locked));
        using (var ev = JsonDocument.Parse(await EventAsync(AttendanceLockEvents.Locked, o.Locked)))
        {
            Assert.Equal(run, ev.RootElement.GetProperty("runId").GetGuid());
            Assert.Contains(employment, ev.RootElement.GetProperty("employmentIds").EnumerateArray().Select(e => e.GetGuid()));
        }

        // Reopening directly while the run stays open, or approved by the requester, is refused (CR-095, FR-XM-32).
        var stillOpen = await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().ReopenAsync(o.Locked, "Wrong punches", new PeriodReopenApproval(o.Hr, o.Payroll, null), null, Ct));
        Assert.Equal("errors.hrm.attendance.lock.run_open", stillOpen.ErrorKey);
        var own = await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().ReopenAsync(o.Locked, "Wrong punches", new PeriodReopenApproval(o.Hr, o.Hr, null), null, Ct));
        Assert.Equal("errors.hrm.attendance.lock.own_approval", own.ErrorKey);

        // The request to cancel the run and reopen: the requester never approves; the payroll manager does; the run is cancelled and the period opens.
        _h.Clock.Advance(TimeSpan.FromMinutes(5));
        await PunchAsync(o, employment, days[3], "08:50", "18:10"); // arrives while locked
        var asked = await _h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/reopen-request", new { reason = "Device punches of the week were missing" }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var instance = asked.Json.GetProperty("instanceId").GetGuid();
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Hr, $"/api/wf/tasks/{await TaskOfAsync(instance)}/approve", new { comment = "mine" }, Ct)).Status);
        await ApproveAsync(o.Payroll, instance);
        Assert.Equal("cancelled", await _h.ScalarAsync<string>("select status from hrm.pay_runs where id = $1", Ct, run));
        Assert.Equal("open|true|Device punches of the week were missing|true|true", await _h.ScalarAsync<string>(
            "select status || '|' || (reopened_at is not null) || '|' || reopen_reason || '|' || (reopen_workflow_instance_id is null) || '|' || (locked_at is null and locked_by_run_id is null) from hrm.attendance_periods where pay_period_id = $1", Ct, o.Locked));
        using (var ev = JsonDocument.Parse(await EventAsync(AttendanceLockEvents.Reopened, o.Locked)))
        {
            Assert.Equal((run, o.Hr, o.Payroll), (ev.RootElement.GetProperty("runId").GetGuid(), ev.RootElement.GetProperty("requestedBy").GetGuid(), ev.RootElement.GetProperty("approvedBy").GetGuid()));
            Assert.Contains(employment, ev.RootElement.GetProperty("employmentIds").EnumerateArray().Select(e => e.GetGuid()));
        }

        // The punch that arrived while locked is worked out once open (re-queued with the reopening).
        Assert.Equal(1, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.days_requeued' and payload->>'employmentId' = $1::text and payload->>'reason' = 'period_reopened'", employment));
        Assert.False(await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().IsLockedAsync(employment, days[2], Ct)));

        // A new run locks again; once that run reaches locked the period never reopens.
        var second = await RunAsync(o, o.Locked, "calculated");
        Assert.Equal(PeriodLockOutcome.Locked, (await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().LockAsync(new PeriodLockRequest(o.Locked, second, true, o.Hr), null, Ct))).Outcome);
        var again = await _h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/reopen-request", new { reason = "Asked anew" }, Ct); // a new request, not the decided one again
        Assert.True(again.Status == HttpStatusCode.Accepted, again.Body);
        Assert.NotEqual(instance, again.Json.GetProperty("instanceId").GetGuid());
        var rejected = await _h.PostAsync(o.Payroll, $"/api/wf/tasks/{await TaskOfAsync(again.Json.GetProperty("instanceId").GetGuid())}/reject", new { comment = "No" }, Ct);
        Assert.True(rejected.Status == HttpStatusCode.OK, rejected.Body);
        Assert.Equal("locked|true", await _h.ScalarAsync<string>("select status || '|' || (reopen_workflow_instance_id is null) from hrm.attendance_periods where pay_period_id = $1", Ct, o.Locked));
        await _h.ExecAsync("update hrm.pay_runs set status = 'locked' where id = $1", Ct, second);
        var final = await _h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/reopen-request", new { reason = "Too late" }, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.lock.run_final"), (final.Status, final.Text("title")));
        Assert.Equal("ck_attendance_periods_lock", (await RefusedAsync("update hrm.attendance_periods set status = 'open', locked_at = null, lock_kind = null, locked_by = null, locked_by_run_id = null, unprocessed_accepted = null, reopened_at = now(), reopen_reason = 'x' where pay_period_id = $1", o.Locked)).ConstraintName);
    }

    // ------------------------------------------------------------------ retroactive adjustments and their impact (FR-XM-26, GHR-04)

    [Fact]
    public async Task FR_XM_26_GHR_04_FR_XM_32_a_three_day_backdated_correction_shows_its_impact_and_is_carried_into_the_next_open_period_without_editing_the_locked_days()
    {
        var o = await OrgAsync();
        var (employment, user, days) = await WeekAsync(o);
        var three = days[..3];
        var body = new
        {
            employmentId = employment, kind = "attendance", reason = "The device lost the morning punches",
            days = three.Select(d => new { date = Iso(d), result = "present", workedMinutes = 480, lateMinutes = 0 }).ToArray(),
        };

        // Before the lock the change is made directly: the impact says so and nothing can be asked for.
        var before = await _h.PostAsync(o.Hr, $"{Adjustments}/impact", body, Ct);
        Assert.True(before.Status == HttpStatusCode.OK, before.Body);
        Assert.Equal((false, "errors.hrm.attendance.adjustment.not_locked"), (before.Json.GetProperty("canSubmit").GetBoolean(), before.Json.GetProperty("refusal").GetString()));
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.adjustment.not_locked"), await StatusTitleAsync(_h.PostAsync(o.Hr, Adjustments, body, Ct)));

        await LockByHrAsync(o);

        // GHR-04: the impact names each day (what was locked, what it should be), the modules and both pay periods touched.
        var impact = await _h.PostAsync(o.Hr, $"{Adjustments}/impact", body, Ct);
        Assert.True(impact.Status == HttpStatusCode.OK, impact.Body);
        Assert.True(impact.Json.GetProperty("canSubmit").GetBoolean(), impact.Body);
        var impactDays = impact.Json.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(three.Select(Iso), impactDays.Select(d => d.GetProperty("date").GetString()));
        Assert.All(impactDays, d => Assert.True(d.GetProperty("locked").GetBoolean()));
        Assert.Equal("late|present", impactDays[0].GetProperty("old").GetProperty("result").GetString() + "|" + impactDays[0].GetProperty("new").GetProperty("result").GetString());
        Assert.Equal(impactDays[0].GetProperty("old").GetProperty("overtimeMinutes").GetInt32(), impactDays[0].GetProperty("new").GetProperty("overtimeMinutes").GetInt32()); // what the correction does not state stays
        Assert.Equal([$"{o.Locked}:locked", $"{o.Next}:target"], impact.Json.GetProperty("payPeriods").EnumerateArray().Select(p => p.GetProperty("payPeriodId").GetGuid() + ":" + p.GetProperty("role").GetString()));
        Assert.Equal(["attendance", "payroll"], impact.Json.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        Assert.Single(impact.Json.GetProperty("overtime").EnumerateArray());
        Assert.Single(impact.Json.GetProperty("penalties").EnumerateArray());

        // Asked for (the employee cannot ask: no permission); the requester never approves; the HR administrator does.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(user, Adjustments, body, Ct)).Status);
        var asked = await _h.PostAsync(o.Hr, Adjustments, body, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var adjustment = asked.Json.GetProperty("id").GetGuid();
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.adjustment.waiting"), await StatusTitleAsync(_h.PostAsync(o.Hr, Adjustments, body, Ct)));
        var instance = asked.Json.GetProperty("instanceId").GetGuid();
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Hr, $"/api/wf/tasks/{await TaskOfAsync(instance)}/approve", new { comment = "mine" }, Ct)).Status);
        var daysBefore = await _h.ScalarAsync<string>("select string_agg(id::text || result || row_version, ',' order by id) from hrm.attendance_days where employment_id = $1", Ct, employment);
        await ApproveAsync(o.Approver, instance);

        // Approved into the next open period; the locked days are exactly as they were; payroll reads the adjustment.
        Assert.Equal($"approved|{o.Next}|2|attendance", await _h.ScalarAsync<string>(
            "select status || '|' || target_pay_period_id || '|' || jsonb_array_length(status_history) || '|' || kind from hrm.attendance_adjustments where id = $1", Ct, adjustment));
        Assert.Equal(daysBefore, await _h.ScalarAsync<string>("select string_agg(id::text || result || row_version, ',' order by id) from hrm.attendance_days where employment_id = $1", Ct, employment));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.outbox_messages where event_type = $1 and payload->>'adjustmentId' = $2::text and payload->>'targetPayPeriodId' = $3::text", AttendanceLockEvents.AdjustmentApproved, adjustment, o.Next));
        var inputs = await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendanceAdjustments>().ForPayPeriodAsync([employment], o.Next, Ct));
        var input = Assert.Single(inputs);
        Assert.Equal((adjustment, o.Locked, 3), (input.AdjustmentId, input.OriginalPayPeriodId, input.Days.Count));
        Assert.Equal(("late", "present", 30, 0), (input.Days[0].Old.Result, input.Days[0].New.Result, input.Days[1].Old.LateMinutes, input.Days[1].New.LateMinutes));
        Assert.Empty(await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendanceAdjustments>().ForPayPeriodAsync([employment], o.Locked, Ct)));
        var read = await _h.GetAsync(o.Hr, $"{Adjustments}/{adjustment}", Ct);
        Assert.True(read.Status == HttpStatusCode.OK, read.Body);
        Assert.Equal(3, read.Json.GetProperty("days").GetArrayLength());

        // FR-XM-05/06: an adjustment's facts never change, its history only grows and it is never removed.
        Assert.Equal("ck_attendance_adjustments_rules", (await RefusedAsync("update hrm.attendance_adjustments set reason = 'changed' where id = $1", adjustment)).ConstraintName);
        Assert.Equal("ck_attendance_adjustments_rules", (await RefusedAsync("update hrm.attendance_adjustments set status = 'submitted', status_history = status_history || '[{\"status\":\"submitted\",\"at\":\"x\"}]'::jsonb where id = $1", adjustment)).ConstraintName);
        Assert.Equal("ck_attendance_adjustments_rules", (await RefusedAsync("delete from hrm.attendance_adjustments where id = $1", adjustment)).ConstraintName);

        // An overtime correction of a locked day is an adjustment too.
        var ot = await _h.PostAsync(o.Hr, Adjustments, new { employmentId = employment, kind = "overtime", reason = "Approved late", days = new[] { new { date = Iso(days[4]), overtimeMinutes = 45 } } }, Ct);
        Assert.True(ot.Status == HttpStatusCode.Accepted, ot.Body);
    }

    [Fact]
    public async Task FR_XM_26_FR_XM_06_a_locked_day_corrected_again_starts_from_its_approved_correction_and_one_correction_of_a_day_waits_at_a_time()
    {
        // Review S1.31 finding 1: a second correction of the same locked day is paid only for its own difference, never again against the locked record.
        var o = await OrgAsync();
        var (employment, _, days) = await WeekAsync(o);
        await LockByHrAsync(o);
        object Body(string result, int late) => new
        {
            employmentId = employment, kind = "attendance", reason = "Corrected from the gate register",
            days = new[] { new { date = Iso(days[1]), result, workedMinutes = 480, lateMinutes = late } },
        };

        var first = await _h.PostAsync(o.Hr, Adjustments, Body("present", 0), Ct);
        Assert.True(first.Status == HttpStatusCode.Accepted, first.Body);
        await ApproveAsync(o.Approver, first.Json.GetProperty("instanceId").GetGuid());
        _h.Clock.Advance(TimeSpan.FromMinutes(5)); // asked later, as a further correction always is

        // The impact of a further correction shows the approved correction as the day's result, not the locked "late".
        var impact = await _h.PostAsync(o.Hr, $"{Adjustments}/impact", Body("late", 10), Ct);
        Assert.True(impact.Status == HttpStatusCode.OK, impact.Body);
        Assert.Equal(("present", 0, "late", 10), (impact.Json.GetProperty("days")[0].GetProperty("old").GetProperty("result").GetString(), impact.Json.GetProperty("days")[0].GetProperty("old").GetProperty("lateMinutes").GetInt32(),
            impact.Json.GetProperty("days")[0].GetProperty("new").GetProperty("result").GetString(), impact.Json.GetProperty("days")[0].GetProperty("new").GetProperty("lateMinutes").GetInt32()));
        var second = await _h.PostAsync(o.Hr, Adjustments, Body("late", 10), Ct);
        Assert.True(second.Status == HttpStatusCode.Accepted, second.Body);
        await ApproveAsync(o.Approver, second.Json.GetProperty("instanceId").GetGuid());

        // Payroll reads both, chained: late to present, then present to late with 10 minutes; together the day ends late by 10 minutes, once.
        var inputs = await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendanceAdjustments>().ForPayPeriodAsync([employment], o.Next, Ct));
        Assert.Equal(["late>present:30>0", "present>late:0>10"], inputs.Select(i => i.Days.Single()).Select(d => $"{d.Old.Result}>{d.New.Result}:{d.Old.LateMinutes}>{d.New.LateMinutes}"));

        // While one correction of a day waits, no other of that day is asked, of any kind (an attendance correction also states the day's overtime).
        var waiting = await _h.PostAsync(o.Hr, Adjustments, new { employmentId = employment, kind = "attendance", reason = "Overtime from the register", days = new[] { new { date = Iso(days[2]), result = "present", workedMinutes = 540, overtimeMinutes = 60 } } }, Ct);
        Assert.True(waiting.Status == HttpStatusCode.Accepted, waiting.Body);
        Assert.Equal((HttpStatusCode.Conflict, AttendanceAdjustmentWaiting), await StatusTitleAsync(_h.PostAsync(o.Hr, Adjustments, new { employmentId = employment, kind = "overtime", reason = "Approved late", days = new[] { new { date = Iso(days[2]), overtimeMinutes = 60 } } }, Ct)));
    }

    [Fact]
    public async Task FR_XM_26_FR_HRM_ATT_21_a_period_with_waiting_or_approved_adjustments_does_not_reopen_and_an_adjustment_of_a_reopened_period_is_not_approved()
    {
        // Review S1.31 finding 2: the recalculated run of a reopened period pays its days directly, so their adjustments must not be paid in the next period too.
        var o = await OrgAsync();
        var (employment, _, days) = await WeekAsync(o);
        var run = await RunAsync(o, o.Locked, "calculated");
        Assert.Equal(PeriodLockOutcome.Locked, (await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().LockAsync(new PeriodLockRequest(o.Locked, run, true, o.Hr), null, Ct))).Outcome);
        object Body(DateOnly day) => new { employmentId = employment, kind = "attendance", reason = "Missing punches", days = new[] { new { date = Iso(day), result = "present", workedMinutes = 480, lateMinutes = 0 } } };

        var waiting = await _h.PostAsync(o.Hr, Adjustments, Body(days[1]), Ct);
        Assert.True(waiting.Status == HttpStatusCode.Accepted, waiting.Body);
        var waitingInstance = waiting.Json.GetProperty("instanceId").GetGuid();
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.attendance.lock.adjustments_open"), await StatusTitleAsync(_h.PostAsync(o.Hr, $"{Periods}/{o.Locked}/reopen-request", new { reason = "Recalculate" }, Ct)));
        var console = await _h.GetAsync(o.Hr, $"{Periods}/{o.Locked}", Ct);
        Assert.True(console.Status == HttpStatusCode.OK, console.Body);
        Assert.Contains("errors.hrm.attendance.lock.adjustments_open", console.Body, StringComparison.Ordinal);

        // The contract refuses it too (S1.38 cancels a run through it), even once the run is cancelled.
        await _h.ExecAsync("update hrm.pay_runs set status = 'cancelled' where id = $1", Ct, run);
        var refused = await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().ReopenAsync(o.Locked, "Recalculate", new PeriodReopenApproval(o.Hr, o.Payroll, null), null, Ct));
        Assert.Equal((PeriodLockOutcome.Refused, "errors.hrm.attendance.lock.adjustments_open"), (refused.Outcome, refused.ErrorKey));

        // The period opens (as the reopen would, once nothing waits): the waiting adjustment can then no longer be approved; it is rejected or withdrawn.
        await _h.ExecAsync("update hrm.attendance_periods set status = 'open', locked_at = null, lock_kind = null, locked_by = null, locked_by_run_id = null, unprocessed_accepted = null, exceptions_accepted = null, reopened_at = now(), reopen_reason = 'test' where pay_period_id = $1", Ct, o.Locked);
        var approve = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(waitingInstance)}/approve", new { comment = "agreed" }, Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "errors.hrm.attendance.adjustment.period_not_locked"), (approve.Status, approve.Text("title"))); // the workflow engine answers a refused decision with 400
        Assert.Equal("submitted", await _h.ScalarAsync<string>("select status from hrm.attendance_adjustments where id = $1", Ct, waiting.Json.GetProperty("id").GetGuid()));
        var rejected = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(waitingInstance)}/reject", new { comment = "The period reopened" }, Ct);
        Assert.True(rejected.Status == HttpStatusCode.OK, rejected.Body);
        Assert.Equal("rejected", await _h.ScalarAsync<string>("select status from hrm.attendance_adjustments where id = $1", Ct, waiting.Json.GetProperty("id").GetGuid()));
        Assert.Empty(await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendanceAdjustments>().ForPayPeriodAsync([employment], o.Next, Ct)));

        // Locked again by a new run, an approved adjustment keeps the period locked: payroll carries it into the next period.
        var second = await RunAsync(o, o.Locked, "calculated");
        Assert.Equal(PeriodLockOutcome.Locked, (await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().LockAsync(new PeriodLockRequest(o.Locked, second, true, o.Hr), null, Ct))).Outcome);
        var approved = await _h.PostAsync(o.Hr, Adjustments, Body(days[0]), Ct);
        Assert.True(approved.Status == HttpStatusCode.Accepted, approved.Body);
        await ApproveAsync(o.Approver, approved.Json.GetProperty("instanceId").GetGuid());
        await _h.ExecAsync("update hrm.pay_runs set status = 'cancelled' where id = $1", Ct, second);
        Assert.Equal("errors.hrm.attendance.lock.adjustments_open", (await InCustomerScopeAsync(sp => sp.GetRequiredService<IAttendancePeriodLock>().ReopenAsync(o.Locked, "Recalculate", new PeriodReopenApproval(o.Hr, o.Payroll, null), null, Ct))).ErrorKey);
    }

    // ------------------------------------------------------------------ locked leave and accruals (FR-HRM-LVE-07, FR-XM-26, R-146)

    [Fact]
    public async Task FR_HRM_LVE_07_FR_XM_26_R_146_locked_unpaid_leave_never_changes_its_cancellation_becomes_an_adjustment_and_accruals_still_post()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var (unpaid, unpaidCode) = await LeaveTypeAsync(o, "UNP", paid: false);
        var (casual, casualCode) = await LeaveTypeAsync(o, "CAS", paid: true);
        var from = o.Monday.AddDays(-120);
        var (policy, _) = await _h.ActivePolicyAsync(o.Hr, o.Company, "leave", new
        {
            types = new object[]
            {
                new { leaveType = unpaidCode, entitlement = new { model = "manual" } },
                new { leaveType = casualCode, entitlement = new { model = "monthly_accrual", quantity = 1, proRata = "none" } },
            },
        }, from, Ct);
        var set = await _h.ActivateSetAsync(o.Hr, o.Company, "leave", from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        var grant = await _h.PostAsync(o.Hr, $"{Employments}/{employment}/leave-ledger", new { leaveTypeId = unpaid, kind = "grant", quantity = 10, entryDate = Iso(o.Monday.AddDays(-20)), reason = "Granted" }, Ct);
        Assert.True(grant.Status == HttpStatusCode.Created, grant.Body);
        var tuesday = o.Monday.AddDays(1);
        var asked = await _h.PostAsync(user, "/api/hrm/leave-requests", new { leaveTypeId = unpaid, startDate = Iso(tuesday), endDate = Iso(tuesday.AddDays(1)), reason = "Family" }, Ct);
        Assert.True(asked.Status is HttpStatusCode.Created or HttpStatusCode.Accepted, asked.Body);
        var request = asked.Json.GetProperty("id").GetGuid();
        var leaveInstance = await _h.ScalarAsync<Guid>("select workflow_instance_id from hrm.leave_requests where id = $1", Ct, request);
        await ApproveAsync(o.Approver, leaveInstance);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.leave_requests where id = $1", Ct, request));

        await LockByHrAsync(o);

        // The leave days and their usage are frozen in the database; payroll's feed shows them locked.
        Assert.Equal("ck_attendance_period_locked", (await RefusedAsync("update hrm.leave_request_days set quantity = 0.5 where leave_request_id = $1", request)).ConstraintName);
        Assert.Equal("ck_attendance_period_locked", (await RefusedAsync("update hrm.leave_request_days set request_status = 'cancelled' where leave_request_id = $1", request)).ConstraintName);
        Assert.Equal("ck_attendance_period_locked", (await RefusedAsync("""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, source_type, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, 'usage', -1, 'test', now(), $7)
            """, Guid.CreateVersion7(), o.Company, employment, unpaid, tuesday.Year, o.Monday.AddDays(3), o.Hr)).ConstraintName);
        var deductions = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILeaveDeductions>().ForAsync([employment], tuesday, tuesday.AddDays(1), Ct));
        Assert.Equal(2, deductions.Count);
        Assert.All(deductions, d => Assert.True(d.Locked));

        // R-146: entitlement postings are not attendance: the accrual job still posts its month-end accrual in the locked month.
        await using (var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct))
        {
            var worker = rig.Worker();
            var run = await rig.QueueAsync("hrm.leave_accrual", new { employment_id = employment }, ct: Ct);
            await rig.RunUntilIdleAsync(worker, Ct);
            Assert.True(await rig.StatusAsync(run, Ct) == "succeeded", string.Join("\n", await rig.LogsAsync(run, Ct)));
        }

        var monthEnd = new DateOnly(o.Monday.Year, o.Monday.Month, DateTime.DaysInMonth(o.Monday.Year, o.Monday.Month));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2 and entry_date = $3 and quantity > 0", employment, casual, monthEnd));

        // Cancelling the locked leave is refused and points to an adjustment; as an adjustment it shows the leave given back, then gives it back in the next open period.
        var cancel = await _h.PostAsync(user, $"/api/hrm/leave-requests/{request}/cancel", new { reason = "Came to work after all" }, Ct);
        Assert.Equal((HttpStatusCode.Conflict, "errors.hrm.leave.period_locked"), (cancel.Status, cancel.Text("title")));
        Assert.Contains("leave_cancellation", cancel.Body, StringComparison.Ordinal);
        var body = new { employmentId = employment, kind = "leave_cancellation", leaveRequestId = request, reason = "Came to work after all", days = new[] { new { date = Iso(tuesday), result = "present" } } };
        var impact = await _h.PostAsync(o.Hr, $"{Adjustments}/impact", body, Ct);
        Assert.True(impact.Status == HttpStatusCode.OK, impact.Body);
        var leave = impact.Json.GetProperty("leave");
        Assert.Equal((2m, 8m, 10m), (leave.GetProperty("givenBack").GetDecimal(), leave.GetProperty("balanceBefore").GetDecimal(), leave.GetProperty("balanceAfter").GetDecimal()));
        Assert.Equal(["attendance", "leave", "payroll"], impact.Json.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        var days = impact.Json.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal((1m, 0m, "present"), (days[0].GetProperty("old").GetProperty("unpaidLeave").GetDecimal(), days[0].GetProperty("new").GetProperty("unpaidLeave").GetDecimal(), days[0].GetProperty("new").GetProperty("result").GetString()));
        var adjusted = await _h.PostAsync(o.Hr, Adjustments, body, Ct);
        Assert.True(adjusted.Status == HttpStatusCode.Accepted, adjusted.Body);
        await ApproveAsync(o.Approver, adjusted.Json.GetProperty("instanceId").GetGuid());

        var nextStart = await _h.ScalarAsync<DateOnly>("select start_date from hrm.pay_periods where id = $1", Ct, o.Next);
        Assert.Equal($"usage_reversal|2.000000|{Iso(nextStart)}", await _h.ScalarAsync<string>(
            "select l.kind || '|' || l.quantity || '|' || l.entry_date from hrm.leave_ledger l join hrm.attendance_adjustments a on a.leave_reversal_id = l.id where a.id = $1", Ct, adjusted.Json.GetProperty("id").GetGuid()));
        Assert.Equal("cancelled", await _h.ScalarAsync<string>("select status from hrm.leave_requests where id = $1", Ct, request));
        Assert.Equal("approved|approved", await _h.ScalarAsync<string>("select string_agg(request_status, '|' order by leave_date) from hrm.leave_request_days where leave_request_id = $1", Ct, request)); // the locked days stay
        Assert.Equal(10m, await _h.ScalarAsync<decimal>("select coalesce(sum(quantity), 0) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2", Ct, employment, unpaid));
    }

    private async Task<(Guid Id, string Code)> LeaveTypeAsync(Org o, string prefix, bool paid)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var created = await _h.PostAsync(o.Hr, "/api/hrm/leave-types", new
        {
            companyId = o.Company, code, nameI18n = new { en = "Leave " + code, ur = "رخصت " + code }, isPaid = paid,
            counting = "calendar_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = !paid,
        }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        return (created.Json.GetProperty("id").GetGuid(), code);
    }

    // ------------------------------------------------------------------ permissions and data scope

    [Fact]
    public async Task FR_XM_26_the_lock_console_and_adjustments_need_their_permissions_and_stay_in_the_callers_companies()
    {
        var o = await OrgAsync();
        var (employment, user, days) = await WeekAsync(o);
        var nobody = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Periods}?companyId={o.Company}", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Periods}/{o.Locked}/lock-request", new { reason = "x", acceptOpenItems = true }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Periods}/{o.Locked}/reopen-request", new { reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(user, $"{Periods}/{o.Locked}/lock-request", new { reason = "x", acceptOpenItems = true }, Ct)).Status); // an employee
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Adjustments}/impact", new { employmentId = employment, kind = "attendance", days = new[] { new { date = Iso(days[0]), result = "present" } } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, Adjustments, Ct)).Status);

        // An HR user of another company sees neither the period nor the employment.
        var other = await _h.CompanyAsync(Ct);
        var elsewhere = await _h.UserAsync(Ct);
        var codes = await _h.ScalarAsync<string[]>("select array_agg(code order by code) from iam.permissions where code like 'hrm.attendance%'", Ct);
        var theirs = await _h.ScopeAsync("companies", new { company_ids = new[] { other } }, Ct);
        await _h.AssignAsync(elsewhere, await _h.RoleAsync(Ct, codes, [("*", theirs)]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(elsewhere, $"{Periods}/{o.Locked}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(elsewhere, $"{Periods}/{o.Locked}/lock-request", new { reason = "x", acceptOpenItems = true }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(elsewhere, $"{Adjustments}/impact", new { employmentId = employment, kind = "attendance", days = new[] { new { date = Iso(days[0]), result = "present" } } }, Ct)).Status);
        var list = await _h.GetAsync(elsewhere, $"{Periods}?companyId={o.Company}", Ct);
        Assert.True(list.Status == HttpStatusCode.OK, list.Body);
        Assert.Equal(0, list.Json.GetProperty("items").GetArrayLength());

        // Checks of the input are field errors; the generated writes of both tables are closed.
        var bad = await _h.PostAsync(o.Hr, Adjustments, new { employmentId = employment, kind = "attendance", reason = "x", days = new[] { new { date = Iso(days[0]), result = "asleep", workedMinutes = 2000 } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("errors.hrm.attendance.adjustment.result", bad.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.attendance.adjustment.minutes", bad.Body, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.Created, (await _h.PostAsync(o.Hr, "/api/hrm/attendance-periods", new { companyId = o.Company }, Ct)).Status);
        Assert.NotEqual(HttpStatusCode.Created, (await _h.PostAsync(o.Hr, "/api/hrm/attendance-adjustments", new { companyId = o.Company }, Ct)).Status);
    }
}
