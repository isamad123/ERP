#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Erp.Hrm.Contracts.Attendance;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// The attendance engine of slice S1.29a through the real API pipeline, the real access engine, the real job worker and dispatcher and a real
/// PostgreSQL: FR-HRM-ATT-09 (the policy decides, none means unprocessed), FR-HRM-ATT-10 (a fortnight of scenarios through the job: on time, late,
/// missing out punch, absent, holiday, weekly off from the roster and from the pattern, leave and duty (test doubles of S1.32b and S1.30), a night
/// shift owning the next morning's punch, a duplicate punch, a punch outside the window, punches on a day with no shift; each with its explanation;
/// a second run writes nothing), FR-HRM-ATT-11 (one exception for a missing out punch; a correction after an approved regularization (double of
/// S1.30) re-queues the day and resolves it; the queue lists unmapped device punches), FR-HRM-ATT-16 (a seasonal shift row named by the roster line
/// decides), GHR-03 (a day as known at a moment), FR-XM-26 (a locked day is not processed again; a resumed run adds no row) and the access rules.
/// Dates are relative to the company's today, so the tests hold on any day.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceEngineTests(TenancyFixture fx) : IAsyncLifetime
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

    /// <summary>The customer's numbers of the test policy; none of them is a product default (AS-4).</summary>
    private static readonly object PolicyParameters = new
    {
        lateGraceMinutes = 10, earlyGraceMinutes = 10, lateHalfDayAfterMinutes = 120, earlyHalfDayAfterMinutes = 120, absentBelowMinutes = 240,
        breakMode = "fixed", breakMinutes = 60, roundingMode = "none", shiftWindowMinutes = 120, weeklyOffWork = "record", holidayWork = "record",
    };

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Hr, Guid PayGroup, Guid Day, Guid Night, DateOnly Monday);

    /// <summary>
    /// A company of its own with an HR user holding every HR permission, a monthly pay group with its periods, a day shift (09:00 to 18:00, 420
    /// minutes for a full day) and a night shift (22:00 to 06:00, 390), and, unless <paramref name="policies"/> is false, the attendance policy and
    /// a shift policy whose weekly off is Sunday for every permanent employee. <see cref="Org.Monday"/> is a Monday three weeks back or more.
    /// </summary>
    private async Task<Org> OrgAsync(bool policies = true)
    {
        var company = await _h.CompanyAsync(Ct);
        var hr = await _h.UserAsync(Ct);
        var everyHr = await _h.ScalarAsync<string[]>("select array_agg(code order by code) from iam.permissions where code like 'hrm.%'", Ct);
        await _h.AssignAsync(hr, await _h.RoleAsync(Ct, everyHr, [("*", "all")], [("*", "class:salary", "editable")]), null, _h.Today.AddDays(-1), null, Ct);
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
        var org = new Org(company, place, hr, payGroup, Guid.Empty, Guid.Empty, monday);
        org = org with { Day = await ShiftAsync(org, "fixed", "09:00", "18:00", false, 420), Night = await ShiftAsync(org, "night", "22:00", "06:00", true, 390) };
        if (policies)
        {
            await PolicyAsync(org, "attendance", PolicyParameters);
            await PolicyAsync(org, "shift", new { weeklyOffKind = "fixed", offDays = new[] { 7 } });
        }

        return org;
    }

    private async Task PolicyAsync(Org o, string type, object parameters)
    {
        var from = o.Monday.AddDays(-60);
        var (policy, _) = await _h.ActivePolicyAsync(o.Hr, o.Company, type, parameters, from, Ct);
        var set = await _h.ActivateSetAsync(o.Hr, o.Company, type, from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    private async Task<Guid> ShiftAsync(Org o, string type, string start, string end, bool crosses, int full)
    {
        var code = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var answer = await _h.PostAsync(o.Hr, "/api/hrm/shifts", new
        {
            companyId = o.Company, code, nameI18n = new { en = "Shift " + code, ur = "شفٹ " + code }, shiftType = type, startTime = start, endTime = end, crossesMidnight = crosses, minFullDayMinutes = full,
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> HireAsync(Org o, bool payGroup = true)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(o.Hr, Employments, new
        {
            personId = person, companyId = o.Company, employeeType = "permanent", joiningDate = Iso(o.Monday.AddDays(-30)),
            assignment = new { orgUnitId = o.Place.OrgUnit, branchId = o.Place.Branch, title = "Officer", payGroupId = payGroup ? o.PayGroup : (Guid?)null },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    /// <summary>Publishes a roster for the days given (working with a shift, weekly off or holiday); days not given have no line.</summary>
    private async Task RosterAsync(Org o, params (Guid Employment, DateOnly Date, string Type, Guid? Shift)[] days)
    {
        var first = days.Min(d => d.Date);
        var last = days.Max(d => d.Date);
        var created = await _h.PostAsync(o.Hr, "/api/hrm/rosters", new { companyId = o.Company, name = "Roster " + Guid.NewGuid().ToString("N")[..6], orgUnitId = o.Place.OrgUnit, periodStart = Iso(first), periodEnd = Iso(last) }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var roster = created.Json.GetProperty("id").GetGuid();
        var changed = await _h.PutAsync(o.Hr, $"/api/hrm/rosters/{roster}/days", new { changes = days.Select(d => new { employmentId = d.Employment, workDate = Iso(d.Date), dayType = d.Type, shiftId = d.Shift, reason = d.Type == "working" ? null : "test" }).ToArray() }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.rosters where id = $1", Ct, roster);
        var published = await _h.PostAsync(o.Hr, $"/api/hrm/rosters/{roster}/publish", new { rowVersion = version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
    }

    private static DateTimeOffset At(DateOnly d, string hhmm, int seconds = 0) =>
        new(d.ToDateTime(TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture)).AddSeconds(seconds), TimeSpan.FromHours(5));

    /// <summary>Punches entered by HR through the event store (S1.27a); returns the stored event ids in order.</summary>
    private async Task<List<Guid>> PunchAsync(Org o, Guid employment, params (DateTimeOffset At, string Direction)[] punches)
    {
        var answer = await _h.PostAsync(o.Hr, "/api/hrm/attendance/events", new
        {
            events = punches.Select(p => new { employmentId = employment, eventAt = p.At, direction = p.Direction, source = "hr_entry", idempotencyKey = "t:" + Guid.NewGuid().ToString("N") }).ToArray(),
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return [.. answer.Json.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("eventId").GetGuid())];
    }

    /// <summary>Approved leave and duty by employment: the test doubles of S1.32b's and S1.30's requests.</summary>
    private static readonly ConcurrentDictionary<Guid, List<ApprovedLeaveDay>> Leave = new();
    private static readonly ConcurrentDictionary<Guid, List<ApprovedDutyDay>> Duty = new();

    private sealed class LeaveDouble : IApprovedLeaveDays
    {
        public Task<IReadOnlyList<ApprovedLeaveDay>> ForAsync(Guid employmentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ApprovedLeaveDay>>([.. Leave.GetValueOrDefault(employmentId, []).Where(d => d.Date >= firstDay && d.Date <= lastDay)]);
    }

    private sealed class DutyDouble : IApprovedDutyDays
    {
        public Task<IReadOnlyList<ApprovedDutyDay>> ForAsync(Guid employmentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ApprovedDutyDay>>([.. Duty.GetValueOrDefault(employmentId, []).Where(d => d.Date >= firstDay && d.Date <= lastDay)]);
    }

    /// <summary>The test double of the attendance lock (S1.31 brings the real lock): every date is locked.</summary>
    private sealed class EverythingLocked : IAttendancePeriodLock
    {
        public Task<bool> IsLockedAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static void Doubles(IServiceCollection s, bool locked)
    {
        s.RemoveAll<IApprovedLeaveDays>();
        s.AddSingleton<IApprovedLeaveDays, LeaveDouble>();
        s.RemoveAll<IApprovedDutyDays>();
        s.AddSingleton<IApprovedDutyDays, DutyDouble>();
        if (locked)
        {
            s.RemoveAll<IAttendancePeriodLock>();
            s.AddScoped<IAttendancePeriodLock, EverythingLocked>();
        }
    }

    /// <summary>Runs <c>hrm.attendance_process</c> in a real worker with the doubles; <paramref name="prepare"/> may change the queued run first.</summary>
    private async Task<List<string>> RunJobAsync(object parameters, bool locked = false, Func<JobRig, Guid, Task>? prepare = null)
    {
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker(extra: s => Doubles(s, locked));
        var run = await rig.QueueAsync("hrm.attendance_process", parameters, ct: Ct);
        if (prepare is not null)
        {
            await prepare(rig, run);
        }

        await rig.RunUntilIdleAsync(worker, Ct);
        var logs = await rig.LogsAsync(run, Ct);
        Assert.True(await rig.StatusAsync(run, Ct) == "succeeded", string.Join("\n", logs));
        return logs;
    }

    private sealed record DayRow(string Date, string Result, string DayType, int Worked, int Late, int Early, string Key, string Rule, string? FirstIn);

    private async Task<Dictionary<string, DayRow>> DaysAsync(Guid employment)
    {
        await using var c = await _h.OwnerAsync(null, Ct);
        await using var cmd = new NpgsqlCommand("""
            select work_date::text, result, day_type, worked_minutes, late_minutes, early_minutes, explanation->>'key', explanation->>'rule',
                   to_char(first_in at time zone 'Asia/Karachi', 'YYYY-MM-DD HH24:MI')
            from hrm.attendance_days where employment_id = $1 and superseded_at is null order by work_date
            """, c);
        cmd.Parameters.AddWithValue(employment);
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<string, DayRow>();
        while (await r.ReadAsync(Ct))
        {
            rows[r.GetString(0)] = new DayRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));
        }

        return rows;
    }

    private Task<long> CountAsync(string sql, params object?[] values) => _h.ScalarAsync<long>(sql, Ct, values);

    // ------------------------------------------------------------------ FR-HRM-ATT-10: the scenario table through the job

    [Fact]
    public async Task FR_HRM_ATT_10_a_fortnight_of_scenarios_through_the_job_gives_each_day_its_result_and_explanation_and_a_second_run_writes_nothing()
    {
        var o = await OrgAsync();
        var e = await HireAsync(o);
        var d = Enumerable.Range(0, 14).Select(i => o.Monday.AddDays(i)).ToArray();
        await RosterAsync(o,
            (e, d[0], "working", o.Day), (e, d[1], "working", o.Day), (e, d[2], "working", o.Day), (e, d[3], "working", o.Day), (e, d[4], "holiday", null),
            (e, d[5], "weekly_off", null), // d[6], Sunday: no line, the shift policy's pattern makes it a weekly off
            (e, d[7], "working", o.Day), (e, d[8], "working", o.Day), (e, d[9], "working", o.Night), (e, d[10], "working", o.Day), (e, d[11], "working", o.Day));
        // d[12], Saturday: no line and not off in the pattern (unscheduled); d[13], Sunday: weekly off by the pattern.
        // The doubles name real request rows (the day keeps a reference to them); S1.32b and S1.30 will approve such requests.
        var leaveRequest = (Guid)(await _h.RowAsync("hrm.leave_requests", o.Company, new Dictionary<string, object?> { ["employment_id"] = e, ["status"] = "approved" }, Ct))["id"]!;
        var dutyRequest = (Guid)(await _h.RowAsync("hrm.duty_requests", o.Company, new Dictionary<string, object?> { ["employment_id"] = e, ["status"] = "approved", ["kind"] = "official_visit" }, Ct))["id"]!;
        Leave[e] = [new ApprovedLeaveDay(d[7], leaveRequest, 1m, "full")];
        Duty[e] = [new ApprovedDutyDay(d[8], dutyRequest, "official_visit")];
        await PunchAsync(o, e,
            (At(d[0], "09:00"), "in"), (At(d[0], "18:00"), "out"),
            (At(d[1], "09:22"), "in"), (At(d[1], "18:00"), "out"),
            (At(d[2], "09:05"), "in"),
            (At(d[4], "10:00"), "in"), (At(d[4], "14:00"), "out"),
            (At(d[7], "09:00"), "in"),
            (At(d[9], "22:00"), "in"), (At(d[10], "06:00"), "out"),
            (At(d[10], "09:00"), "in"), (At(d[10], "09:00", 20), "in"), (At(d[10], "18:00"), "out"),
            (At(d[11], "05:00"), "in"), (At(d[11], "09:00"), "in"), (At(d[11], "18:00"), "out"),
            (At(d[12], "10:00"), "in"), (At(d[12], "14:00"), "out"));

        var logs = await RunJobAsync(new { employment_id = e, from = Iso(d[0]), to = Iso(d[13]) });
        Assert.Contains(logs, l => l.Contains("written 14", StringComparison.Ordinal));
        var days = await DaysAsync(e);
        void Is(int i, string result, string rule, int worked = 0, int late = 0, string? dayType = null)
        {
            var row = days[Iso(d[i])];
            Assert.True(row.Result == result && row.Rule == rule && row.Worked == worked && row.Late == late, $"day {i}: {row}");
            Assert.Equal(dayType ?? "working", row.DayType);
            Assert.StartsWith("hrm.att.explain.", row.Key, StringComparison.Ordinal);
        }

        Is(0, "present", "present", 480);
        Is(1, "late", "late", 458, 22);
        Is(2, "incomplete", "missing_out");
        Is(3, "absent", "absent_no_punch");
        Is(4, "holiday", "holiday", 180, dayType: "holiday");
        Is(5, "weekly_off", "weekly_off", dayType: "weekly_off");
        Is(6, "weekly_off", "weekly_off", dayType: "weekly_off");
        Is(7, "on_leave", "on_leave");
        Is(8, "on_duty", "on_duty");
        Is(9, "present", "present", 420);
        Is(10, "present", "present", 480);
        Is(11, "present", "present", 480);
        Is(12, "incomplete", "unscheduled", 180);
        Is(13, "weekly_off", "weekly_off", dayType: "weekly_off");
        Assert.Equal(Iso(d[10]) + " 09:00", days[Iso(d[10])].FirstIn); // the 06:00 punch belongs to the night shift before it (ruling Q3)

        // The explanation of the late day names the rule's values and the policy version; the repeated punch is named, not counted.
        var late = await _h.GetAsync(o.Hr, $"{Employments}/{e}/attendance-days/{Iso(d[1])}/explain", Ct);
        Assert.True(late.Status == HttpStatusCode.OK, late.Body);
        var current = late.Json.GetProperty("current");
        Assert.Equal(22, current.GetProperty("explanation").GetProperty("params").GetProperty("late").GetInt32());
        Assert.Equal("09:22", current.GetProperty("explanation").GetProperty("params").GetProperty("firstIn").GetString());
        Assert.Equal(1, current.GetProperty("explanation").GetProperty("policy").GetProperty("version").GetInt32());
        Assert.Equal(current.GetProperty("policyVersionId").GetGuid().ToString(), current.GetProperty("explanation").GetProperty("policy").GetProperty("versionId").GetString());
        Assert.Equal("preview", late.Json.GetProperty("preview").GetProperty("status").GetString()); // worked out again, nothing stored
        var dup = (await _h.GetAsync(o.Hr, $"{Employments}/{e}/attendance-days/{Iso(d[10])}/explain", Ct)).Json.GetProperty("current").GetProperty("explanation");
        Assert.Contains(dup.GetProperty("steps").EnumerateArray(), s => s.GetProperty("key").GetString() == "hrm.att.step.not_counted" && s.GetProperty("params").GetProperty("count").GetInt32() == 1);

        // Exceptions: one per kind and day; each raised once with its event.
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_exceptions where employment_id = $1 and work_date = $2 and kind = 'missing_out' and status = 'open'", e, d[2]));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_exceptions where employment_id = $1 and work_date = $2 and kind = 'outside_tolerance' and status = 'open'", e, d[11]));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_exceptions where employment_id = $1 and work_date = $2 and kind = 'unscheduled' and status = 'open'", e, d[12]));
        Assert.Equal(3, await CountAsync("select count(*) from hrm.attendance_exceptions where employment_id = $1", e));
        Assert.Equal(3, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.exception_raised' and payload->>'employmentId' = $1", e.ToString()));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_periods where pay_group_id = $1 and status = 'open' and $2 between period_start and period_end", o.PayGroup, d[0]));

        // Same inputs, same results: a second run writes no row and raises nothing.
        var again = await RunJobAsync(new { employment_id = e, from = Iso(d[0]), to = Iso(d[13]) });
        Assert.Contains(again, l => l.Contains("written 0, unchanged 14", StringComparison.Ordinal));
        Assert.Equal(14, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", e));
        Assert.Equal(3, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.exception_raised' and payload->>'employmentId' = $1", e.ToString()));
        Assert.True(await CountAsync("select count(*) from aud.change_log where entity_type = 'hrm.attendance_days' and entity_id in (select id from hrm.attendance_days where employment_id = $1)", e) >= 14); // audited
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-11, GHR-03: exceptions and re-queueing

    [Fact]
    public async Task FR_HRM_ATT_11_GHR_03_a_missing_out_punch_raises_one_exception_and_after_an_approved_regularization_the_correction_requeues_the_day_and_clears_it()
    {
        var o = await OrgAsync();
        var e = await HireAsync(o);
        var day = o.Monday.AddDays(1);
        await RosterAsync(o, (e, day, "working", o.Day));
        await PunchAsync(o, e, (At(day, "09:02"), "in"));
        var processed = await _h.PostAsync(o.Hr, $"{Employments}/{e}/attendance-days/process", new { from = Iso(day), to = Iso(day) }, Ct);
        Assert.True(processed.Status == HttpStatusCode.OK, processed.Body);
        Assert.Equal("written", processed.Json.GetProperty("days")[0].GetProperty("status").GetString());
        Assert.Equal("incomplete", processed.Json.GetProperty("days")[0].GetProperty("result").GetString());
        var queue = await _h.GetAsync(o.Hr, $"/api/hrm/attendance/exceptions?companyId={o.Company}", Ct);
        Assert.True(queue.Status == HttpStatusCode.OK, queue.Body);
        var item = Assert.Single(queue.Json.GetProperty("items").EnumerateArray());
        Assert.Equal("missing_out", item.GetProperty("kind").GetString());
        Assert.Equal(e, item.GetProperty("employmentId").GetGuid());

        // Unmapped device punches of the company (S1.27b) are in the same queue for those who may read them.
        await _h.RowAsync("hrm.unmapped_punches", o.Company, new Dictionary<string, object?> { ["status"] = "open", ["employment_id"] = null, ["resolved_at"] = null, ["resolved_by"] = null }, Ct);
        queue = await _h.GetAsync(o.Hr, $"/api/hrm/attendance/exceptions?companyId={o.Company}", Ct);
        Assert.Equal(1, queue.Json.GetProperty("unmapped").GetProperty("count").GetInt32());
        Assert.Equal("unmapped", queue.Json.GetProperty("unmapped").GetProperty("items")[0].GetProperty("kind").GetString());
        var before = _h.Clock.UtcNow;
        _h.Clock.Advance(TimeSpan.FromMinutes(5));

        // S1.30's approval (double here): the request is approved and the correction is a new out punch; the late punch re-queues the day.
        await _h.RowAsync("hrm.regularization_requests", o.Company, new Dictionary<string, object?>
        {
            ["employment_id"] = e, ["work_date"] = day, ["status"] = "approved", ["exception_id"] = item.GetProperty("id").GetGuid(), ["workflow_instance_id"] = null,
            ["requested_in"] = null, ["requested_out"] = At(day, "18:00").ToUniversalTime(), ["file_ids"] = Array.Empty<Guid>(),
        }, Ct);
        await PunchAsync(o, e, (At(day, "18:00"), "out"));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.days_requeued' and aggregate_id = $1", e));
        await using (var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct))
        {
            var dispatcher = rig.Provider().GetRequiredService<EventDispatcher>();
            for (var pass = 0; pass < 40 && await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", e) < 2; pass++)
            {
                await dispatcher.DispatchOnceAsync(_h.Code, Ct);
            }
        }

        // A new row version holds the new result; the first one is kept, marked replaced; the exception is resolved as regularized.
        Assert.Equal(2, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1 and work_date = $2", e, day));
        var days = await DaysAsync(e);
        Assert.Equal("present", days[Iso(day)].Result);
        Assert.Equal("regularized", await _h.ScalarAsync<string>("select status from hrm.attendance_exceptions where employment_id = $1", Ct, e));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_exceptions where employment_id = $1", e));
        queue = await _h.GetAsync(o.Hr, $"/api/hrm/attendance/exceptions?companyId={o.Company}", Ct);
        Assert.Empty(queue.Json.GetProperty("items").EnumerateArray());

        // GHR-03: the day as it was known before the correction, and its whole history.
        var asOf = await _h.GetAsync(o.Hr, $"{Employments}/{e}/attendance-days?from={Iso(day)}&to={Iso(day)}&asOf={Uri.EscapeDataString(before.ToString("O", CultureInfo.InvariantCulture))}", Ct);
        Assert.True(asOf.Status == HttpStatusCode.OK, asOf.Body);
        Assert.Equal("incomplete", Assert.Single(asOf.Json.GetProperty("days").EnumerateArray()).GetProperty("result").GetString());
        var history = await _h.GetAsync(o.Hr, $"{Employments}/{e}/attendance-days?from={Iso(day)}&to={Iso(day)}&history=true", Ct);
        Assert.Equal(["incomplete", "present"], history.Json.GetProperty("days").EnumerateArray().Select(x => x.GetProperty("result").GetString()));
        Assert.NotEqual(JsonValueKind.Null, history.Json.GetProperty("days")[0].GetProperty("supersededAt").ValueKind);
    }

    // ------------------------------------------------------------------ FR-XM-26: locked days; resumed runs

    [Fact]
    public async Task FR_XM_26_a_locked_day_is_not_processed_again_and_an_interrupted_run_resumed_adds_no_row()
    {
        var o = await OrgAsync();
        var a = await HireAsync(o);
        var b = await HireAsync(o);
        var c = await HireAsync(o);
        var day = o.Monday.AddDays(2);
        await RosterAsync(o, (a, day, "working", o.Day), (b, day, "working", o.Day), (c, day, "working", o.Day));
        foreach (var e in new[] { a, b, c })
        {
            await PunchAsync(o, e, (At(day, "09:00"), "in"), (At(day, "18:00"), "out"));
        }

        var unit = new { org_unit_id = o.Place.OrgUnit, from = Iso(day), to = Iso(day) };
        Assert.Contains(await RunJobAsync(unit), l => l.Contains("3 employments: written 3", StringComparison.Ordinal));

        // A run stopped after the first employment (its checkpoint saved) resumes with the others and adds nothing: same inputs, same rows.
        var ids = new[] { a, b, c }.Order().ToArray();
        var resumed = await RunJobAsync(unit, prepare: (rig, run) => rig.ExecAsync("update job.runs set checkpoint = $2::jsonb where id = $1", Ct, null, run, JsonSerializer.Serialize(new { after = ids[0].ToString("D") })));
        Assert.Contains(resumed, l => l.Contains("2 employments: written 0, unchanged 2", StringComparison.Ordinal));
        Assert.Equal(3, await CountAsync("select count(*) from hrm.attendance_days where employment_id = any($1)", ids));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.attendance_days where employment_id = any($1) and superseded_at is not null", ids));

        // The period is locked (double of S1.31): a later punch is stored but the day is not worked out again.
        await PunchAsync(o, a, (At(day, "12:00"), "out"));
        var locked = await RunJobAsync(new { employment_id = a, from = Iso(day), to = Iso(day) }, locked: true);
        Assert.Contains(locked, l => l.Contains("written 0, unchanged 0, skipped 1 (hrm.att.skip.locked 1)", StringComparison.Ordinal));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", a));
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-09: no policy, no pay period

    [Fact]
    public async Task FR_HRM_ATT_09_without_an_attendance_policy_or_a_pay_period_a_day_stays_unprocessed_and_the_reason_is_explained()
    {
        var o = await OrgAsync(policies: false);
        var e = await HireAsync(o);
        var day = o.Monday.AddDays(3);
        await RosterAsync(o, (e, day, "working", o.Day));
        await PunchAsync(o, e, (At(day, "09:00"), "in"), (At(day, "18:00"), "out"));
        var processed = await _h.PostAsync(o.Hr, $"{Employments}/{e}/attendance-days/process", new { from = Iso(day), to = Iso(day) }, Ct);
        Assert.True(processed.Status == HttpStatusCode.OK, processed.Body);
        Assert.Equal("skipped", processed.Json.GetProperty("days")[0].GetProperty("status").GetString());
        Assert.Equal("hrm.att.skip.no_policy", processed.Json.GetProperty("days")[0].GetProperty("reason").GetString());
        var explain = await _h.GetAsync(o.Hr, $"{Employments}/{e}/attendance-days/{Iso(day)}/explain", Ct);
        Assert.Equal("hrm.att.skip.no_policy", explain.Json.GetProperty("preview").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, explain.Json.GetProperty("current").ValueKind);
        Assert.Equal(0, await CountAsync("select count(*) from hrm.attendance_days where employment_id = $1", e));

        // With a policy but no pay group the result cannot be stored (period_id); the preview still shows what it would be.
        await PolicyAsync(o, "attendance", PolicyParameters);
        var noGroup = await HireAsync(o, payGroup: false);
        await RosterAsync(o, (noGroup, day, "working", o.Day));
        await PunchAsync(o, noGroup, (At(day, "09:00"), "in"), (At(day, "18:00"), "out"));
        var skipped = await _h.PostAsync(o.Hr, $"{Employments}/{noGroup}/attendance-days/process", new { from = Iso(day), to = Iso(day) }, Ct);
        Assert.Equal("hrm.att.skip.no_pay_period", skipped.Json.GetProperty("days")[0].GetProperty("reason").GetString());
        var preview = (await _h.GetAsync(o.Hr, $"{Employments}/{noGroup}/attendance-days/{Iso(day)}/explain", Ct)).Json.GetProperty("preview");
        Assert.Equal("present", preview.GetProperty("result").GetString());

        // The range is checked: order, length and the future.
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(o.Hr, $"{Employments}/{e}/attendance-days/process", new { from = Iso(day), to = Iso(day.AddDays(-1)) }, Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(o.Hr, $"{Employments}/{e}/attendance-days/process", new { from = Iso(day.AddDays(-70)), to = Iso(day) }, Ct)).Status);
        var future = await _h.PostAsync(o.Hr, $"{Employments}/{e}/attendance-days/process", new { from = Iso(Today), to = Iso(Today.AddDays(1)) }, Ct);
        Assert.Equal("errors.hrm.attendance.process_future", future.Json.GetProperty("errors").GetProperty("to")[0].GetString());
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-16: the shift row the roster names decides

    [Fact]
    public async Task FR_HRM_ATT_16_a_seasonal_shift_named_by_the_roster_line_decides_lateness_on_its_days()
    {
        var o = await OrgAsync();
        var e = await HireAsync(o);
        var ramzan = await ShiftAsync(o, "fixed", "08:00", "14:00", false, 240);
        var normal = o.Monday.AddDays(1);
        var seasonal = o.Monday.AddDays(2);
        await RosterAsync(o, (e, normal, "working", o.Day), (e, seasonal, "working", ramzan));
        await PunchAsync(o, e, (At(normal, "08:30"), "in"), (At(normal, "18:00"), "out"), (At(seasonal, "08:30"), "in"), (At(seasonal, "14:00"), "out"));
        var processed = await _h.PostAsync(o.Hr, $"{Employments}/{e}/attendance-days/process", new { from = Iso(normal), to = Iso(seasonal) }, Ct);
        Assert.True(processed.Status == HttpStatusCode.OK, processed.Body);
        var days = await DaysAsync(e);
        Assert.Equal(("present", 0), (days[Iso(normal)].Result, days[Iso(normal)].Late));
        Assert.Equal(("late", 30), (days[Iso(seasonal)].Result, days[Iso(seasonal)].Late));
    }

    // ------------------------------------------------------------------ access and the database

    [Fact]
    public async Task FR_HRM_ATT_10_permissions_and_data_scope_decide_who_processes_and_reads_days_and_the_database_keeps_results()
    {
        var o = await OrgAsync();
        var e = await HireAsync(o);
        var day = o.Monday.AddDays(1);
        await RosterAsync(o, (e, day, "working", o.Day));
        await PunchAsync(o, e, (At(day, "09:00"), "in"));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Hr, $"{Employments}/{e}/attendance-days/process", new { from = Iso(day), to = Iso(day) }, Ct)).Status);

        // Reading without processing: refused to process.
        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.attendance_days.read", "hrm.attendance_exceptions.read"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, $"{Employments}/{e}/attendance-days/process", new { from = Iso(day), to = Iso(day) }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(reader, $"{Employments}/{e}/attendance-days?from={Iso(day)}&to={Iso(day)}", Ct)).Status);

        // No read permission: refused; own scope only: someone else's employment does not exist, and the queue holds nothing of theirs.
        var nobody = await _h.UserAsync(Ct);
        await _h.AssignAsync(nobody, await _h.RoleAsync(Ct, ["hrm.shifts.read"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Employments}/{e}/attendance-days", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, "/api/hrm/attendance/exceptions", Ct)).Status);
        var own = await _h.UserAsync(Ct);
        await _h.AssignAsync(own, await _h.RoleAsync(Ct, ["hrm.attendance_days.read", "hrm.attendance_exceptions.read", "hrm.attendance_days.process"], [("*", "own")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(own, $"{Employments}/{e}/attendance-days", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(own, $"{Employments}/{e}/attendance-days/{Iso(day)}/explain", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(own, $"{Employments}/{e}/attendance-days/process", new { from = Iso(day), to = Iso(day) }, Ct)).Status);
        var queue = await _h.GetAsync(own, $"/api/hrm/attendance/exceptions?companyId={o.Company}", Ct);
        Assert.True(queue.Status == HttpStatusCode.OK, queue.Body);
        Assert.Empty(queue.Json.GetProperty("items").EnumerateArray());

        // The generated writes are closed; the database refuses to change or delete a result, and allows only marking it replaced once.
        Assert.Contains((await _h.PostAsync(o.Hr, "/api/hrm/attendance-days", new { companyId = o.Company }, Ct)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        var id = await _h.ScalarAsync<Guid>("select id from hrm.attendance_days where employment_id = $1", Ct, e);
        var change = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.attendance_days set result = 'present' where id = $1", Ct, id));
        Assert.Equal("ck_attendance_days_versions", change.ConstraintName);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("delete from hrm.attendance_days where id = $1", Ct, id));
        Assert.Equal("ck_attendance_days_versions", delete.ConstraintName);
        await _h.ExecAsync("update hrm.attendance_days set superseded_at = now() where id = $1", Ct, id);
        var twice = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.attendance_days set superseded_at = now() + interval '1 day' where id = $1", Ct, id));
        Assert.Equal("ck_attendance_days_versions", twice.ConstraintName);
    }
}
