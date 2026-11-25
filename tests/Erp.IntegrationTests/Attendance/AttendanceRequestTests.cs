#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// The attendance requests of slice S1.30a through the real API pipeline, the real access engine, the real workflow engine, the real event
/// dispatcher and a real PostgreSQL: FR-HRM-ATT-11 (forgot to punch: request, approval by someone else, correction punch that supersedes nothing it
/// should not touch, the day recalculated, the exception regularized; a rejection keeps the exception open), FR-HRM-ATT-14 (a field duty of three days
/// makes them on duty; work from home does not count until the policy says so), GHR-09 (HR submits for another person; the acting user is recorded),
/// FR-XM-26 (a locked date is refused) and FR-XM-32 (nobody approves their own request, nor one made for them). Dates come from the company's day.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceRequestTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private const string Regularizations = "/api/hrm/attendance/regularization-requests";
    private const string Duties = "/api/hrm/attendance/duty-requests";
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

    private static readonly object PolicyParameters = new
    {
        lateGraceMinutes = 10, earlyGraceMinutes = 10, absentBelowMinutes = 240, breakMode = "fixed", breakMinutes = 60, roundingMode = "none",
        shiftWindowMinutes = 120, weeklyOffWork = "record", holidayWork = "record",
    };

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Hr, Guid Approver, Guid PayGroup, Guid Day, DateOnly Monday);

    /// <summary>A company with an HR user, an approver (baseline HR Admin), a pay group with periods, a day shift 09:00 to 18:00 and the attendance policy.</summary>
    private async Task<Org> OrgAsync(object? policy = null)
    {
        var company = await _h.CompanyAsync(Ct);
        var hr = await _h.UserAsync(Ct);
        var everyHr = await _h.ScalarAsync<string[]>("select array_agg(code order by code) from iam.permissions where code like 'hrm.%'", Ct);
        await _h.AssignAsync(hr, await _h.RoleAsync(Ct, everyHr, [("*", "all")], [("*", "class:salary", "editable")]), null, _h.Today.AddDays(-1), null, Ct);
        var approver = await _h.UserAsync(Ct, null, "hr_admin");
        var monday = Today.AddDays(-21);
        while (monday.DayOfWeek != DayOfWeek.Monday)
        {
            monday = monday.AddDays(-1);
        }

        var code = "PG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var group = await _h.PostAsync(hr, "/api/hrm/pay-groups", new { companyId = company, code, nameI18n = new { en = "Group " + code, ur = "گروپ " + code }, frequency = "monthly", taxYearStartMonth = 1, cutOffDay = 25, paymentDay = 28 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var payGroup = group.Json.GetProperty("id").GetGuid();
        foreach (var year in new[] { monday.AddDays(-40).Year, monday.AddDays(20).Year }.Distinct())
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
        await PolicyAsync(org, "attendance", policy ?? PolicyParameters);
        return org;
    }

    private async Task PolicyAsync(Org o, string type, object parameters)
    {
        var from = o.Monday.AddDays(-60);
        var (policy, _) = await _h.ActivePolicyAsync(o.Hr, o.Company, type, parameters, from, Ct);
        var set = await _h.ActivateSetAsync(o.Hr, o.Company, type, from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    /// <summary>An employment that belongs to a new user holding <paramref name="role"/> (the employee role, scope own, by default).</summary>
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

    private async Task RosterAsync(Org o, Guid employment, params DateOnly[] days)
    {
        var created = await _h.PostAsync(o.Hr, "/api/hrm/rosters", new { companyId = o.Company, name = "Roster " + Guid.NewGuid().ToString("N")[..6], orgUnitId = o.Place.OrgUnit, periodStart = Iso(days.Min()), periodEnd = Iso(days.Max()) }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var roster = created.Json.GetProperty("id").GetGuid();
        var changed = await _h.PutAsync(o.Hr, $"/api/hrm/rosters/{roster}/days", new { changes = days.Select(d => new { employmentId = employment, workDate = Iso(d), dayType = "working", shiftId = o.Day, reason = (string?)null }).ToArray() }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.rosters where id = $1", Ct, roster);
        var published = await _h.PostAsync(o.Hr, $"/api/hrm/rosters/{roster}/publish", new { rowVersion = version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
    }

    private static DateTimeOffset At(DateOnly d, string hhmm) =>
        new(d.ToDateTime(TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture)), TimeSpan.FromHours(5));

    private async Task PunchAsync(Org o, Guid employment, params (DateTimeOffset At, string Direction)[] punches)
    {
        var answer = await _h.PostAsync(o.Hr, "/api/hrm/attendance/events", new
        {
            events = punches.Select(p => new { employmentId = employment, eventAt = p.At, direction = p.Direction, source = "hr_entry", idempotencyKey = "t:" + Guid.NewGuid().ToString("N") }).ToArray(),
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
    }

    private async Task<Answer> ProcessAsync(Org o, Guid employment, DateOnly from, DateOnly to) =>
        await _h.PostAsync(o.Hr, $"{Employments}/{employment}/attendance-days/process", new { from = Iso(from), to = Iso(to) }, Ct);

    private async Task DispatchAsync(Func<Task<bool>> done)
    {
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var dispatcher = rig.Provider().GetRequiredService<EventDispatcher>();
        for (var pass = 0; pass < 40 && !await done().ConfigureAwait(false); pass++)
        {
            await dispatcher.DispatchOnceAsync(_h.Code, Ct);
        }
    }

    private Task<long> CountAsync(string sql, params object?[] values) => _h.ScalarAsync<long>(sql, Ct, values);

    private Task<Guid> TaskOfAsync(Answer request) =>
        _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, request.Json.GetProperty("instanceId").GetGuid());

    /// <summary>A day with an in punch only: processed, with its open missing-out exception; returns the employment, user, day and exception.</summary>
    private async Task<(Guid Employment, Guid User, DateOnly Day, Guid Exception)> ForgotToPunchAsync(Org o, int dayOffset)
    {
        var (employment, user) = await HireAsync(o);
        var day = o.Monday.AddDays(dayOffset);
        await RosterAsync(o, employment, day);
        await PunchAsync(o, employment, (At(day, "09:02"), "in"));
        var processed = await ProcessAsync(o, employment, day, day);
        Assert.True(processed.Status == HttpStatusCode.OK, processed.Body);
        var exception = await _h.ScalarAsync<Guid>("select id from hrm.attendance_exceptions where employment_id = $1 and status = 'open'", Ct, employment);
        return (employment, user, day, exception);
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-11

    [Fact]
    public async Task FR_HRM_ATT_11_FR_XM_32_forgot_to_punch_request_approval_by_another_person_makes_the_day_present_and_the_exception_regularized_and_the_original_punch_is_untouched()
    {
        var o = await OrgAsync();
        var (employment, user, day, exception) = await ForgotToPunchAsync(o, 1);
        var originalIn = await _h.ScalarAsync<Guid>("select id from hrm.attendance_events where employment_id = $1 and direction = 'in'", Ct, employment);

        var asked = await _h.PostAsync(user, Regularizations, new { exceptionId = exception, requestedOut = At(day, "18:00"), reason = "Forgot to punch out" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        Assert.False(asked.Json.GetProperty("onBehalf").GetBoolean());
        Assert.Equal("submitted", await _h.ScalarAsync<string>("select status from hrm.regularization_requests where id = $1", Ct, asked.Json.GetProperty("id").GetGuid()));
        var task = await TaskOfAsync(asked);

        // Nobody approves their own request (FR-XM-32); the pending request changes nothing yet.
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(user, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.Equal(0, await CountAsync("select count(*) from hrm.attendance_events where employment_id = $1 and source = 'regularization'", employment));
        Assert.Equal("open", await _h.ScalarAsync<string>("select status from hrm.attendance_exceptions where id = $1", Ct, exception));

        var approved = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.regularization_requests where id = $1", Ct, asked.Json.GetProperty("id").GetGuid()));

        // The correction is a new punch from the request; the original punch is as it was (the database refuses edits of events).
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_events where employment_id = $1 and source = 'regularization' and direction = 'out' and supersedes_event_id is null", employment));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_events where id = $1 and direction = 'in' and source = 'hr_entry' and not is_duplicate", originalIn));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.days_requeued' and aggregate_id = $1", employment));

        await DispatchAsync(async () => await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", employment) >= 2);
        Assert.Equal("present", await _h.ScalarAsync<string>("select result from hrm.attendance_days where employment_id = $1 and work_date = $2 and superseded_at is null", Ct, employment, day));
        Assert.Equal("regularized", await _h.ScalarAsync<string>("select status from hrm.attendance_exceptions where id = $1", Ct, exception));
    }

    [Fact]
    public async Task FR_HRM_ATT_11_a_corrected_in_punch_is_a_new_event_that_supersedes_the_original_which_stays_stored_and_a_rejection_keeps_the_exception_open()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var day = o.Monday.AddDays(2);
        await RosterAsync(o, employment, day);
        await PunchAsync(o, employment, (At(day, "11:30"), "in"), (At(day, "18:00"), "out"));
        Assert.Equal("half_day", await ProcessedResultAsync(o, employment, day));
        var original = await _h.ScalarAsync<Guid>("select id from hrm.attendance_events where employment_id = $1 and direction = 'in'", Ct, employment);

        var asked = await _h.PostAsync(user, Regularizations, new { workDate = Iso(day), requestedIn = At(day, "09:00"), reason = "The gate system was down" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        var rejected = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(asked)}/reject", new { comment = "no proof" }, Ct);
        Assert.True(rejected.Status == HttpStatusCode.OK, rejected.Body);
        Assert.Equal("rejected", await _h.ScalarAsync<string>("select status from hrm.regularization_requests where id = $1", Ct, asked.Json.GetProperty("id").GetGuid()));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.attendance_events where employment_id = $1 and source = 'regularization'", employment));

        var again = await _h.PostAsync(user, Regularizations, new { workDate = Iso(day), requestedInTime = "09:00:00", reason = "The gate system was down, proof attached to HR" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, again.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(again)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal(original, await _h.ScalarAsync<Guid>("select supersedes_event_id from hrm.attendance_events where employment_id = $1 and source = 'regularization' and direction = 'in'", Ct, employment));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_events where id = $1", original));
        await DispatchAsync(async () => await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", employment) >= 2);
        Assert.Equal("present", await _h.ScalarAsync<string>("select result from hrm.attendance_days where employment_id = $1 and work_date = $2 and superseded_at is null", Ct, employment, day));
    }

    private async Task<string> ProcessedResultAsync(Org o, Guid employment, DateOnly day)
    {
        var processed = await ProcessAsync(o, employment, day, day);
        Assert.True(processed.Status == HttpStatusCode.OK, processed.Body);
        return processed.Json.GetProperty("days")[0].GetProperty("result").GetString()!;
    }

    // ------------------------------------------------------------------ GHR-09, FR-XM-32, scope and checks

    [Fact]
    public async Task GHR_09_FR_XM_32_hr_submits_for_another_person_with_the_acting_user_recorded_and_neither_the_acting_user_nor_the_person_approves_it()
    {
        var o = await OrgAsync();
        var (employment, user, day, _) = await ForgotToPunchAsync(o, 3);
        var (otherEmployment, otherUser) = await HireAsync(o);

        var asked = await _h.PostAsync(o.Approver, Regularizations, new { employmentId = employment, workDate = Iso(day), requestedOut = At(day, "18:00"), reason = "HR enters it for the employee" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        Assert.True(asked.Json.GetProperty("onBehalf").GetBoolean());
        var row = asked.Json.GetProperty("id").GetGuid();
        Assert.Equal(o.Approver, await _h.ScalarAsync<Guid>("select created_by from hrm.regularization_requests where id = $1", Ct, row));
        Assert.Equal(employment, await _h.ScalarAsync<Guid>("select employment_id from hrm.regularization_requests where id = $1", Ct, row));
        var task = await TaskOfAsync(asked);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status); // the acting user
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(user, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status); // the person it is for
        Assert.Equal("submitted", await _h.ScalarAsync<string>("select status from hrm.regularization_requests where id = $1", Ct, row));

        // An employee (scope: own) cannot ask for a colleague: the colleague does not exist for them.
        var colleague = await _h.PostAsync(otherUser, Regularizations, new { employmentId = employment, workDate = Iso(day), requestedOut = At(day, "18:00"), reason = "For someone else" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, colleague.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(otherUser, Duties, new { employmentId = employment, kind = "on_duty", startDate = Iso(day), endDate = Iso(day), purpose = "For someone else" }, Ct)).Status);
        Assert.NotEqual(Guid.Empty, otherEmployment);

        // A person with no right to ask is refused.
        var nobody = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Regularizations, new { employmentId = employment, workDate = Iso(day) }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Duties, new { employmentId = employment }, Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_ATT_11_requests_are_checked_one_pending_request_a_day_times_in_order_no_future_day_and_a_reason()
    {
        var o = await OrgAsync();
        var (employment, user, day, exception) = await ForgotToPunchAsync(o, 4);

        var empty = await _h.PostAsync(user, Regularizations, new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Contains("workDate", empty.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        var noTimes = await _h.PostAsync(user, Regularizations, new { workDate = Iso(day), reason = "x" }, Ct);
        Assert.Contains("requestedIn", noTimes.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        var backwards = await _h.PostAsync(user, Regularizations, new { workDate = Iso(day), requestedIn = At(day, "18:00"), requestedOut = At(day, "09:00"), reason = "x" }, Ct);
        Assert.Contains("requestedOut", backwards.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        var farTime = await _h.PostAsync(user, Regularizations, new { workDate = Iso(day), requestedIn = At(day.AddDays(5), "09:00"), reason = "x" }, Ct);
        Assert.Contains("requestedIn", farTime.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        var future = await _h.PostAsync(user, Regularizations, new { workDate = Iso(Today.AddDays(3)), requestedIn = At(Today.AddDays(3), "09:00"), reason = "x" }, Ct);
        Assert.Contains("workDate", future.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        var wrongException = await _h.PostAsync(user, Regularizations, new { exceptionId = exception, workDate = Iso(day.AddDays(1)), requestedOut = At(day, "18:00"), reason = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongException.Status);

        var first = await _h.PostAsync(user, Regularizations, new { exceptionId = exception, requestedOut = At(day, "18:00"), reason = "Forgot" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        var second = await _h.PostAsync(user, Regularizations, new { exceptionId = exception, requestedOut = At(day, "18:05"), reason = "Forgot again" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal(1, await CountAsync("select count(*) from hrm.regularization_requests where employment_id = $1", employment));
    }

    [Fact]
    public async Task FR_XM_26_a_request_for_a_date_in_a_locked_period_is_refused_with_a_clear_message()
    {
        var o = await OrgAsync();
        var (employment, user, day, exception) = await ForgotToPunchAsync(o, 1);
        await _h.ExecAsync("update hrm.attendance_periods set status = 'locked', locked_at = now(), lock_kind = 'manual', locked_by = created_by where pay_group_id = $1", Ct, o.PayGroup);

        var refused = await _h.PostAsync(user, Regularizations, new { exceptionId = exception, requestedOut = At(day, "18:00"), reason = "Late" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("errors.hrm.attendance.request.period_locked", refused.Text("title"));
        var duty = await _h.PostAsync(user, Duties, new { kind = "official_visit", startDate = Iso(day), endDate = Iso(day), purpose = "Visit" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duty.Status);
        Assert.Equal("errors.hrm.attendance.request.period_locked", duty.Text("title"));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.regularization_requests where employment_id = $1", employment));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.duty_requests where employment_id = $1", employment));
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-14

    [Fact]
    public async Task FR_HRM_ATT_14_a_field_duty_of_three_days_makes_them_on_duty_and_work_from_home_counts_only_when_the_policy_says_so()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var days = new[] { o.Monday.AddDays(7), o.Monday.AddDays(8), o.Monday.AddDays(9) };
        await RosterAsync(o, employment, days);

        var asked = await _h.PostAsync(user, Duties, new { kind = "field_duty", startDate = Iso(days[0]), endDate = Iso(days[2]), purpose = "Site survey" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        var overlapping = await _h.PostAsync(user, Duties, new { kind = "on_duty", startDate = Iso(days[2]), endDate = Iso(days[2]), purpose = "Same day" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, overlapping.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(asked)}/approve", new { comment = "go" }, Ct)).Status);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.duty_requests where id = $1", Ct, asked.Json.GetProperty("id").GetGuid()));

        var processed = await ProcessAsync(o, employment, days[0], days[2]);
        Assert.True(processed.Status == HttpStatusCode.OK, processed.Body);
        Assert.Equal(["on_duty", "on_duty", "on_duty"], processed.Json.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("result").GetString()));
        Assert.Equal(3, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1 and result = 'on_duty' and duty_request_id = $2 and superseded_at is null", employment, asked.Json.GetProperty("id").GetGuid()));

        // Work from home does not count as present unless the policy says so: the same shape of request leaves the day to the punches (here none: absent).
        var (homeEmployment, homeUser) = await HireAsync(o);
        var home = o.Monday.AddDays(14);
        await RosterAsync(o, homeEmployment, home);
        var wfh = await _h.PostAsync(homeUser, Duties, new { kind = "work_from_home", startDate = Iso(home), endDate = Iso(home), purpose = "Plumber at home" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, wfh.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(wfh)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("absent", await ProcessedResultAsync(o, homeEmployment, home));
    }

    [Fact]
    public async Task FR_HRM_ATT_14_a_policy_that_counts_work_from_home_as_present_makes_the_day_on_duty()
    {
        var o = await OrgAsync(new
        {
            lateGraceMinutes = 10, earlyGraceMinutes = 10, absentBelowMinutes = 240, breakMode = "fixed", breakMinutes = 60, roundingMode = "none",
            shiftWindowMinutes = 120, weeklyOffWork = "record", holidayWork = "record", workFromHomeCountsAsPresent = true,
        });
        var (employment, user) = await HireAsync(o);
        var home = o.Monday.AddDays(7);
        await RosterAsync(o, employment, home);
        var wfh = await _h.PostAsync(user, Duties, new { kind = "work_from_home", startDate = Iso(home), endDate = Iso(home), purpose = "Home day" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, wfh.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(wfh)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("on_duty", await ProcessedResultAsync(o, employment, home));
    }

    [Fact]
    public async Task FR_HRM_ATT_14_a_duty_request_needs_a_known_kind_in_order_days_and_a_purpose()
    {
        var o = await OrgAsync();
        var (_, user) = await HireAsync(o);
        var day = o.Monday.AddDays(7);
        var bad = await _h.PostAsync(user, Duties, new { kind = "holiday", startDate = Iso(day), endDate = Iso(day.AddDays(-1)) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(["endDate", "kind", "purpose"], bad.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ the exception queue's filters (Build item 4)

    [Fact]
    public async Task FR_HRM_ATT_11_the_exception_queue_filters_by_unit_kind_and_age_and_shows_the_age_of_each_item()
    {
        var o = await OrgAsync();
        var (employment, _, day, _) = await ForgotToPunchAsync(o, 1);
        var url = $"/api/hrm/attendance/exceptions?companyId={o.Company}";
        var all = await _h.GetAsync(o.Hr, url, Ct);
        var item = Assert.Single(all.Json.GetProperty("items").EnumerateArray());
        Assert.Equal(Today.DayNumber - day.DayNumber, item.GetProperty("ageDays").GetInt32());

        Assert.Single((await _h.GetAsync(o.Hr, url + $"&unitId={o.Place.OrgUnit}&kind=missing_out&minAgeDays=1", Ct)).Json.GetProperty("items").EnumerateArray());
        Assert.Empty((await _h.GetAsync(o.Hr, url + $"&unitId={Guid.NewGuid()}", Ct)).Json.GetProperty("items").EnumerateArray());
        Assert.Empty((await _h.GetAsync(o.Hr, url + "&kind=late", Ct)).Json.GetProperty("items").EnumerateArray());
        Assert.Empty((await _h.GetAsync(o.Hr, url + "&minAgeDays=" + (Today.DayNumber - day.DayNumber + 1), Ct)).Json.GetProperty("items").EnumerateArray());
        Assert.NotEqual(Guid.Empty, employment);
    }
}
