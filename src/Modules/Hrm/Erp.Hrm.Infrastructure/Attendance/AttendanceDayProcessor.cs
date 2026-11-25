using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Attendance;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// What became of one day: <c>written</c> (a new result row), <c>unchanged</c> (the current result already says the same), <c>skipped</c> with
/// the reason as a localization key (no policy, nothing planned and no punch, locked, no pay period, not employed), or <c>preview</c> (worked
/// out, not stored).
/// </summary>
internal sealed record DayRun(DateOnly Date, string Status, string? Reason, Guid? DayId, string? Result, DayOutcome? Outcome = null)
{
    public const string Written = "written";
    public const string Unchanged = "unchanged";
    public const string Skipped = "skipped";
    public const string Preview = "preview";
}

/// <summary>
/// The attendance engine (slice S1.29 Build items 2 to 4 and 6; FR-HRM-ATT-10, ATT-11, FR-XM-26). For an employment and a range of dates it
/// reads the plan of each day from the effective shift of S1.28b (<see cref="IEffectiveShiftReader"/>, R-158: the published roster line and the
/// shift row it names with a season's times, R-144, else the weekly-off pattern of the shift policy, else a holiday of the calendar),
/// the <c>attendance</c> policy in force on that date (BR-05), approved leave and duty, and the counted punches the day owns, and works the day
/// out with <see cref="DayRules"/>. A result is stored only when it differs from the current one: the current row is marked replaced and a
/// new row is added (history kept, never edited in place; the database allows nothing else), so the same inputs never add a row and a
/// resumed job writes nothing twice. A date in a locked period is never processed again (FR-XM-26). Exceptions follow the newest result:
/// open ones that no longer apply are closed (or marked regularized when an approved regularization exists for the day), new ones are raised
/// with the event <c>hrm.attendance.exception_raised</c>. Each employment is processed by one writer at a time (advisory lock). Overtime entries
/// and lateness penalties then follow the written results (<see cref="AttendanceConsequences"/>, S1.29b).
/// </summary>
internal sealed class AttendanceDayProcessor(
    HrmDbContext db, IPolicyResolver resolver, IEffectiveShiftReader effective, IApprovedLeaveDays leaves, IApprovedDutyDays duties, IAttendancePeriodLock locks, EmploymentApplier applier,
    AttendanceConsequences consequences)
{
    public const string NoRoster = "hrm.att.skip.no_roster";
    public const string Locked = "hrm.att.skip.locked";
    public const string NotEmployed = "hrm.att.skip.not_employed";
    public const string NotFinished = "hrm.att.skip.not_finished";

    /// <summary>Statuses of an exception that stand for "this exception is known": a new one of the same kind is not raised beside them.</summary>
    private static readonly string[] Known = ["open", "accepted", "rejected"];

    private readonly AttendancePolicyReader policies = new(db, resolver);
    private readonly AttendancePeriodLocator periods = new(db);

    /// <summary>Processes (or, with <paramref name="save"/> false, previews) the days from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public async Task<IReadOnlyList<DayRun>> ProcessAsync(Employment e, DateOnly from, DateOnly to, DateTimeOffset now, Guid actor, bool save, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (save)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.attendance_day:" + e.Id.ToString("D")}, 0))", ct).ConfigureAwait(false);
        }

        var zone = await applier.ZoneAsync(e.CompanyId, ct).ConfigureAwait(false);
        // The plan of each day, and of the day before the range (its night shift owns punches after midnight), is S1.28b's effective shift (R-158).
        var effectives = new Dictionary<DateOnly, EffectiveShift?>();
        for (var date = from.AddDays(-1); date <= to; date = date.AddDays(1))
        {
            effectives[date] = await effective.ReadAsync(e.Id, date, ct).ConfigureAwait(false);
        }

        var shiftIds = effectives.Values.Where(x => x?.ShiftId is not null).Select(x => x!.ShiftId!.Value).Distinct().ToList();
        var shifts = await db.Set<Shift>().AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
        var plans = effectives.ToDictionary(x => x.Key, x => PlanOf(x.Value, shifts));

        // Punches of the range: a day owns punches of its own date and, for a night shift, of the next one (ruling Q3).
        var events = await db.Set<AttendanceEvent>().AsNoTracking().Where(x => x.EmploymentId == e.Id && x.EventDate >= from && x.EventDate <= to.AddDays(1))
            .Select(x => new DayPunch(x.Id, x.EventAt, x.Direction, x.IsDuplicate, x.SupersedesEventId)).ToListAsync(ct).ConfigureAwait(false);
        var superseded = (await db.Set<AttendanceEvent>().AsNoTracking().Where(x => x.EmploymentId == e.Id && x.SupersedesEventId != null)
            .Select(x => x.SupersedesEventId!.Value).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var leave = (await leaves.ForAsync(e.Id, from, to, ct).ConfigureAwait(false)).GroupBy(l => l.Date).ToDictionary(g => g.Key, g => g.ToList());
        var duty = (await duties.ForAsync(e.Id, from, to, ct).ConfigureAwait(false)).GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.OrderBy(d => d.DutyRequestId).First());
        var current = await db.Set<AttendanceDay>().AsNoTracking().Where(d => d.EmploymentId == e.Id && d.WorkDate >= from && d.WorkDate <= to && d.SupersededAt == null)
            .ToDictionaryAsync(d => d.WorkDate, ct).ConfigureAwait(false);

        DayShift? ShiftOf(DateOnly date) => plans[date].Shift;

        var runs = new List<DayRun>();

        // A day that is not processed now keeps its last result (S1.31 decides about stale results, R-159), but its open exceptions no longer
        // stand for anything the engine says, so they are closed (review S1.29a finding 8); a locked or unfinished day is left as it is.
        async Task SkipAsync(DateOnly date, string? reason)
        {
            var existing = current.GetValueOrDefault(date);
            runs.Add(new DayRun(date, DayRun.Skipped, reason, existing?.Id, existing?.Result));
            if (save && existing is not null && reason is not (Locked or NotFinished))
            {
                await ExceptionsAsync(e, date, existing.Id, [], now, actor, ct).ConfigureAwait(false);
            }
        }

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (e.Status == "cancelled" || date < e.JoiningDate || (e.SeparationDate is { } last && date > last))
            {
                await SkipAsync(date, NotEmployed).ConfigureAwait(false);
                continue;
            }

            if (await locks.IsLockedAsync(e.Id, date, ct).ConfigureAwait(false))
            {
                await SkipAsync(date, Locked).ConfigureAwait(false);
                continue;
            }

            var plan = plans[date];
            var shiftRow = plan.Shift is { } ps ? shifts[ps.Id] : null;
            var answer = await policies.AtAsync(e.Id, date, shiftRow?.PolicyId, ct).ConfigureAwait(false);
            if (answer.Policy is not { } policy)
            {
                await SkipAsync(date, answer.SkipKey).ConfigureAwait(false);
                continue;
            }

            // The punches this day owns: the previous day's shift keeps the punches its window reaches past midnight (its own policy's window).
            var previous = ShiftOf(date.AddDays(-1));
            var previousWindow = policy.ShiftWindowMinutes;
            if (previous is not null)
            {
                var prevRow = shifts[previous.Id];
                previousWindow = (await policies.AtAsync(e.Id, date.AddDays(-1), prevRow.PolicyId, ct).ConfigureAwait(false)).Policy?.ShiftWindowMinutes ?? policy.ShiftWindowMinutes;
            }

            var (ownFrom, ownTo) = DayRules.OwnedRange(date, plan.Shift, policy.ShiftWindowMinutes, previous, previousWindow);

            // A day is stored only once its owned range has ended (its shift and window are over): an unfinished day would show a false
            // "missing out punch" (review S1.29a findings 1 and 2). The nightly run covers the last two days, so it is stored the night after.
            if (save && now < new DateTimeOffset(ownTo, zone.GetUtcOffset(ownTo)))
            {
                await SkipAsync(date, NotFinished).ConfigureAwait(false);
                continue;
            }

            var owned = events.Where(p => Local(p.At, zone) >= ownFrom && Local(p.At, zone) < ownTo).ToList();
            var counted = DayRules.Counted(owned, superseded);
            if (plan.Source == DayPlan.Unplanned && counted.Count == 0)
            {
                await SkipAsync(date, NoRoster).ConfigureAwait(false);
                continue;
            }

            var outcome = DayRules.Evaluate(new DayInput(date, zone, plan, policy, answer.Reference!,
                [.. leave.GetValueOrDefault(date, []).Select(l => new DayLeave(l.LeaveRequestId, l.Quantity, l.Part, l.Minutes))],
                duty.TryGetValue(date, out var d) ? new DayDuty(d.DutyRequestId, d.Kind, d.StartAt, d.EndAt) : null, counted));
            if (owned.Count > counted.Count)
            {
                // Repeated and corrected punches are named, not counted (the explanation says how many).
                outcome = outcome with
                {
                    Explanation = outcome.Explanation with
                    {
                        Steps = [new ExplanationLine(DayRules.StepNotCounted, new Dictionary<string, object> { ["count"] = owned.Count - counted.Count }), .. outcome.Explanation.Steps],
                    },
                };
            }

            if (!save)
            {
                runs.Add(new DayRun(date, DayRun.Preview, null, null, outcome.Result, outcome));
                continue;
            }

            var period = await periods.PeriodAsync(e.Id, e.CompanyId, date, now, actor, ct).ConfigureAwait(false);
            if (period is not { } periodId)
            {
                await SkipAsync(date, AttendancePeriodLocator.NoPayPeriod).ConfigureAwait(false);
                continue;
            }

            var explanation = outcome.Explanation.ToJson();
            var existing = current.GetValueOrDefault(date);
            if (existing is not null && Same(existing, outcome, explanation, answer.Reference!.VersionId, periodId))
            {
                runs.Add(new DayRun(date, DayRun.Unchanged, null, existing.Id, existing.Result, outcome));
                continue;
            }

            var dayId = await WriteAsync(e, date, outcome, explanation, answer.Reference!.VersionId, periodId, existing, now, actor, ct).ConfigureAwait(false);
            await ExceptionsAsync(e, date, dayId, outcome.Exceptions, now, actor, ct).ConfigureAwait(false);
            runs.Add(new DayRun(date, DayRun.Written, null, dayId, outcome.Result, outcome));
        }

        // S1.29b: overtime entries and lateness penalties follow the written results, in the same transaction.
        if (save)
        {
            await consequences.AfterAsync(e, runs, now, actor, ct).ConfigureAwait(false);
        }

        return runs;
    }

    /// <summary>
    /// The engine's plan of a day from the effective shift (R-158): a roster day (its type, and for a working day the shift row with the season's
    /// start, end and crossing midnight; core times, policy and full-day minutes stay the row's, the full-day minutes scaled to the season's
    /// span by <see cref="DayRules.WithSeason"/>), the shift policy's weekly off, a calendar holiday (day type holiday), or nothing planned. A
    /// rostered working day stays a working day when the calendar has a holiday on it: the published roster decides (S1.28b ruling Q6).
    /// </summary>
    public static DayPlan PlanOf(EffectiveShift? answer, IReadOnlyDictionary<Guid, Shift> shifts)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        switch (answer?.Source)
        {
            case "roster":
                var row = answer.ShiftId is { } id && answer.DayType == DayPlan.Working ? shifts.GetValueOrDefault(id) : null;
                if (row is null)
                {
                    return new DayPlan(DayPlan.FromRoster, answer.DayType, null);
                }

                var shift = ToDayShift(row);
                if (answer.Season is { } season && answer.Start is { } start && answer.End is { } end)
                {
                    return new DayPlan(DayPlan.FromRoster, DayPlan.Working, DayRules.WithSeason(shift, start, end, answer.CrossesMidnight), new DaySeason(season.SeasonName, season.PolicyCode));
                }

                return new DayPlan(DayPlan.FromRoster, DayPlan.Working, shift);
            case "weekly_off_policy":
                return new DayPlan(DayPlan.FromPattern, DayPlan.WeeklyOff, null);
            case "holiday_calendar":
                return new DayPlan(DayPlan.FromCalendar, DayPlan.Holiday, null);
            default:
                return DayPlan.None;
        }
    }

    public static DayShift ToDayShift(Shift s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var segments = ShiftRules.ParseSegments(s.Segments.RootElement) ?? [];
        var work = segments.Where(g => g.Kind == ShiftSegment.Work).ToList();
        var start = s.StartTime ?? (work.Count > 0 ? work[0].Start : TimeOnly.MinValue);
        var end = s.EndTime ?? (work.Count > 0 ? work[^1].End : TimeOnly.MinValue);
        return new DayShift(s.Id, s.Code, s.ShiftType, start, end, s.CoreStart, s.CoreEnd, s.CrossesMidnight, s.MinFullDayMinutes);
    }

    private static DateTime Local(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).DateTime;

    private static bool Same(AttendanceDay d, DayOutcome o, JsonObject explanation, Guid policyVersionId, Guid periodId) =>
        d.DayType == o.DayType && d.Result == o.Result && d.ShiftId == o.ShiftId && d.FirstIn == o.FirstIn && d.LastOut == o.LastOut
        && d.WorkedMinutes == o.WorkedMinutes && d.LateMinutes == o.LateMinutes && d.EarlyMinutes == o.EarlyMinutes && d.OvertimeMinutes == o.OvertimeMinutes
        && d.LeaveRequestId == o.LeaveRequestId && d.DutyRequestId == o.DutyRequestId && d.PolicyVersionId == policyVersionId && d.PeriodId == periodId
        && JsonNode.DeepEquals(JsonNode.Parse(d.Explanation.RootElement.GetRawText()), explanation);

    /// <summary>Marks the current row replaced (its own save, so the one-current-row index never sees two) and adds the new result.</summary>
    private async Task<Guid> WriteAsync(Employment e, DateOnly date, DayOutcome o, JsonObject explanation, Guid policyVersionId, Guid periodId, AttendanceDay? existing, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        if (existing is not null)
        {
            var old = await db.Set<AttendanceDay>().FirstAsync(x => x.Id == existing.Id && x.WorkDate == date, ct).ConfigureAwait(false);
            old.SupersededAt = now;
            old.UpdatedAt = now;
            old.UpdatedBy = actor;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.Entry(old).State = EntityState.Detached;
        }

        var row = new AttendanceDay
        {
            Id = Guid.CreateVersion7(now), CompanyId = e.CompanyId, EmploymentId = e.Id, WorkDate = date, ShiftId = o.ShiftId, DayType = o.DayType, Result = o.Result,
            FirstIn = o.FirstIn, LastOut = o.LastOut, WorkedMinutes = o.WorkedMinutes, LateMinutes = o.LateMinutes, EarlyMinutes = o.EarlyMinutes, OvertimeMinutes = o.OvertimeMinutes,
            LeaveRequestId = o.LeaveRequestId, DutyRequestId = o.DutyRequestId, Explanation = JsonDocument.Parse(explanation.ToJsonString()), PolicyVersionId = policyVersionId,
            PeriodId = periodId, ProcessedAt = now, CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Entry(row).State = EntityState.Detached;
        return row.Id;
    }

    /// <summary>
    /// The day's exceptions follow its newest result (Build item 4): a known exception of a kind still raised moves to the new row; an open one
    /// no longer raised resolves (regularized when an approved regularization exists for the day, otherwise closed); a new kind is raised.
    /// </summary>
    private async Task ExceptionsAsync(Employment e, DateOnly date, Guid dayId, IReadOnlyList<string> kinds, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var rows = await db.Set<AttendanceException>().Where(x => x.EmploymentId == e.Id && x.WorkDate == date && DayResults.EngineKinds.Contains(x.Kind) && Known.Contains(x.Status))
            .ToListAsync(ct).ConfigureAwait(false);
        var regularized = rows.Exists(r => r.Status == "open" && !kinds.Contains(r.Kind))
            && await db.Set<RegularizationRequest>().AsNoTracking().AnyAsync(r => r.EmploymentId == e.Id && r.WorkDate == date && r.Status == "approved", ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (kinds.Contains(row.Kind))
            {
                row.AttendanceDayId = dayId;
            }
            else if (row.Status == "open")
            {
                row.Status = regularized ? "regularized" : "closed";
                row.AttendanceDayId = dayId;
            }
            else
            {
                continue;
            }

            row.UpdatedAt = now;
            row.UpdatedBy = actor;
        }

        foreach (var kind in kinds.Where(k => !rows.Exists(r => r.Kind == k)))
        {
            var id = Guid.CreateVersion7(now);
            db.Add(new AttendanceException
            {
                Id = id, CompanyId = e.CompanyId, EmploymentId = e.Id, WorkDate = date, Kind = kind, AttendanceDayId = dayId, Status = "open",
                CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor,
            });
            applier.RaiseFor(AttendanceEngineEvents.ExceptionRaised, AttendanceEngineEvents.AggregateType, dayId, now, actor, new
            {
                exceptionId = id, employmentId = e.Id, companyId = e.CompanyId, workDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kind,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }
}
