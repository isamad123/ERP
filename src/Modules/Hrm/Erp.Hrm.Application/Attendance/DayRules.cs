using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erp.Hrm.Application.Attendance;

/// <summary>A punch as the engine reads it: its id, when it was made, the direction given at capture, and the correction fields.</summary>
public sealed record DayPunch(Guid Id, DateTimeOffset At, string Direction, bool IsDuplicate = false, Guid? SupersedesEventId = null);

/// <summary>The shift row in force on the day (the row the roster line names, R-144): the times that decide lateness and early leaving.</summary>
public sealed record DayShift(Guid Id, string Code, string ShiftType, TimeOnly Start, TimeOnly End, TimeOnly? CoreStart, TimeOnly? CoreEnd, bool CrossesMidnight, int MinFullDayMinutes);

/// <summary>
/// What the day is planned to be (slice S1.29 Build item 2), as the effective shift of S1.28b answers it (R-158): <see cref="Source"/> is
/// <c>roster</c> (a published roster line, S1.28a), <c>weekly_off_pattern</c> (no line, the shift policy's pattern says off),
/// <c>holiday_calendar</c> (no line, not a weekly off, and a holiday of the employee's calendar) or <c>none</c> (nothing planned: the day is
/// unscheduled). <see cref="Season"/> names the <c>seasonal_timing</c> season whose times the shift carries on this day, if any.
/// </summary>
public sealed record DayPlan(string Source, string DayType, DayShift? Shift, DaySeason? Season = null)
{
    public const string FromRoster = "roster";
    public const string FromPattern = "weekly_off_pattern";
    public const string FromCalendar = "holiday_calendar";
    public const string Unplanned = "none";

    public const string Working = "working";
    public const string WeeklyOff = "weekly_off";
    public const string Holiday = "holiday";

    public static DayPlan None { get; } = new(Unplanned, Working, null);
}

/// <summary>The season of a <c>seasonal_timing</c> policy that changed the shift's times on the day (S1.28b): its name and the policy's code.</summary>
public sealed record DaySeason(string Name, string PolicyCode);

/// <summary>
/// An approved leave day (contract <c>IApprovedLeaveDays</c>): the request, the part of a day (1 = whole day) and which half; hourly leave gives
/// <see cref="Minutes"/> instead (quantity 0), which the day's full-day and absence minutes shrink by (review S1.29a finding 4).
/// </summary>
public sealed record DayLeave(Guid LeaveRequestId, decimal Quantity, string Part, int? Minutes = null)
{
    public const string Full = "full";
    public const string FirstHalf = "first_half";
    public const string SecondHalf = "second_half";
}

/// <summary>
/// An approved duty request touching the day (contract <c>IApprovedDutyDays</c>) with its time. It decides the day only when it covers the whole
/// shift (or the day has no shift); a shorter duty counts as time at work, as if punched in and out at its start and end (CR-089 S1.29a detail m).
/// </summary>
public sealed record DayDuty(Guid DutyRequestId, string Kind, DateTimeOffset? Start = null, DateTimeOffset? End = null);

/// <summary>The attendance policy version that decided (stored on the day as <c>policy_version_id</c> and named in the explanation).</summary>
public sealed record DayPolicyRef(Guid VersionId, string Code, int Version);

/// <summary>Everything one day's result is made from. The same input always gives the same <see cref="DayOutcome"/> (slice Goal).</summary>
public sealed record DayInput(
    DateOnly Date, TimeZoneInfo Zone, DayPlan Plan, AttendancePolicy Policy, DayPolicyRef PolicyRef, IReadOnlyList<DayLeave> Leave, DayDuty? Duty,
    IReadOnlyList<DayPunch> Punches);

/// <summary>One sentence of an explanation: a localization key and its values (ruling Q7: translated at display, English and Urdu).</summary>
public sealed record ExplanationLine(string Key, IReadOnlyDictionary<string, object> Params);

/// <summary>
/// Why a day has its result (FR-HRM-ATT-10): the rule that decided, one summary sentence, the steps that led to it and the policy version.
/// Stored as JSON of keys and values (<see cref="ToJson"/>), never as English text.
/// </summary>
public sealed record DayExplanation(string Rule, ExplanationLine Summary, IReadOnlyList<ExplanationLine> Steps, DayPolicyRef? Policy)
{
    public JsonObject ToJson()
    {
        var steps = new JsonArray();
        foreach (var s in Steps)
        {
            steps.Add(Line(s));
        }

        var o = new JsonObject
        {
            ["rule"] = Rule,
            ["key"] = Summary.Key,
            ["params"] = Params(Summary.Params),
            ["steps"] = steps,
        };
        if (Policy is { } p)
        {
            o["policy"] = new JsonObject { ["code"] = p.Code, ["version"] = p.Version, ["versionId"] = p.VersionId.ToString("D") };
        }

        return o;
    }

    private static JsonObject Line(ExplanationLine l) => new() { ["key"] = l.Key, ["params"] = Params(l.Params) };

    private static JsonObject Params(IReadOnlyDictionary<string, object> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            o[k] = v switch
            {
                int i => JsonValue.Create(i),
                decimal d => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture)),
            };
        }

        return o;
    }
}

/// <summary>
/// The result of one day (columns of <c>hrm.attendance_days</c>) and the exception kinds it raises (FR-HRM-ATT-11). Overtime minutes are
/// S1.29b's; part a records zero.
/// </summary>
public sealed record DayOutcome(
    string DayType, string Result, Guid? ShiftId, DateTimeOffset? FirstIn, DateTimeOffset? LastOut, int WorkedMinutes, int LateMinutes, int EarlyMinutes,
    Guid? LeaveRequestId, Guid? DutyRequestId, DayExplanation Explanation, IReadOnlyList<string> Exceptions);

/// <summary>Result values (enum of <c>hrm.attendance_days.result</c>) and exception kinds (<c>hrm.attendance_exceptions.kind</c>).</summary>
public static class DayResults
{
    public const string Present = "present";
    public const string Absent = "absent";
    public const string Late = "late";
    public const string HalfDay = "half_day";
    public const string OnLeave = "on_leave";
    public const string Holiday = "holiday";
    public const string WeeklyOff = "weekly_off";
    public const string OnDuty = "on_duty";
    public const string Incomplete = "incomplete";

    public const string MissingIn = "missing_in";
    public const string MissingOut = "missing_out";
    public const string OddCount = "odd_count";
    public const string OutsideTolerance = "outside_tolerance";
    public const string Unscheduled = "unscheduled";

    /// <summary>The exception kinds the engine raises and resolves itself (the others are S1.29b's or S1.30's).</summary>
    public static IReadOnlyList<string> EngineKinds { get; } = [MissingIn, MissingOut, OddCount, OutsideTolerance, Unscheduled];
}

/// <summary>
/// The pure rules that turn one employment's day into a result (slice S1.29 Build items 2 and 3; FR-HRM-ATT-09, ATT-10; BR-05). No number of
/// its own: every limit comes from <see cref="AttendancePolicy"/> or the shift. Order (Build item 2): the planned day type (holiday, weekly off),
/// approved leave, approved duty, then punches. Punches: duplicates and corrected ones are not counted; the first is in, the last is out
/// (ruling Q4); middle punches give breaks only with break mode <c>by_punches</c>; an odd count of three or more is an exception. A night shift
/// owns the next day's punches up to its end plus the window (ruling Q3).
/// </summary>
public static class DayRules
{
    // Summary keys (the rule that decided).
    public const string ExplainHoliday = "hrm.att.explain.holiday";
    public const string ExplainWeeklyOff = "hrm.att.explain.weekly_off";
    public const string ExplainOnLeave = "hrm.att.explain.on_leave";
    public const string ExplainOnDuty = "hrm.att.explain.on_duty";
    public const string ExplainAbsentNoPunch = "hrm.att.explain.absent_no_punch";
    public const string ExplainAbsentWorked = "hrm.att.explain.absent_worked";
    public const string ExplainMissingOut = "hrm.att.explain.incomplete_missing_out";
    public const string ExplainMissingIn = "hrm.att.explain.incomplete_missing_in";
    public const string ExplainUnscheduled = "hrm.att.explain.incomplete_unscheduled";
    public const string ExplainHalfDayWorked = "hrm.att.explain.half_day_worked";
    public const string ExplainHalfDayLate = "hrm.att.explain.half_day_late";
    public const string ExplainHalfDayEarly = "hrm.att.explain.half_day_early";
    public const string ExplainLate = "hrm.att.explain.late";
    public const string ExplainPresent = "hrm.att.explain.present";
    public const string ExplainPresentLeftEarly = "hrm.att.explain.present_left_early";

    // Step keys.
    public const string StepShift = "hrm.att.step.shift";
    public const string StepRosterDay = "hrm.att.step.roster_day";
    public const string StepPattern = "hrm.att.step.weekly_off_pattern";
    public const string StepCalendarHoliday = "hrm.att.step.holiday_calendar";
    public const string StepSeason = "hrm.att.step.season";
    public const string StepPunches = "hrm.att.step.punches";
    public const string StepNotCounted = "hrm.att.step.not_counted";
    public const string StepOutsideWindow = "hrm.att.step.outside_window";
    public const string StepBreakFixed = "hrm.att.step.break_fixed";
    public const string StepBreakPunches = "hrm.att.step.break_punches";
    public const string StepRounded = "hrm.att.step.rounded";
    public const string StepWithinGrace = "hrm.att.step.within_grace";
    public const string StepEarly = "hrm.att.step.early";
    public const string StepEarlyWithinGrace = "hrm.att.step.early_within_grace";
    public const string StepFullDay = "hrm.att.step.full_day";
    public const string StepPartLeave = "hrm.att.step.part_leave";
    public const string StepOddCount = "hrm.att.step.odd_count";
    public const string StepRestWorkRecorded = "hrm.att.step.rest_work_recorded";
    public const string StepRestWorkIgnored = "hrm.att.step.rest_work_ignored";
    public const string StepPunchesIgnored = "hrm.att.step.punches_ignored";
    public const string StepPolicy = "hrm.att.step.policy";
    public const string StepDutyPart = "hrm.att.step.duty_part";
    public const string StepHourLeave = "hrm.att.step.hour_leave";

    /// <summary>Prefix of the keys of coded values inside an explanation (a day type, a half, a duty kind), translated at display (review S1.29a finding 6).</summary>
    public const string ValuePrefix = "hrm.att.value.";

    /// <summary>The codes an explanation may carry as values; each has a text <c>hrm.att.value.&lt;code&gt;</c> in both languages.</summary>
    public static IReadOnlyList<string> ValueCodes { get; } =
        ["working", "weekly_off", "holiday", "full", "first_half", "second_half", "shift", "policy", "up", "down", "nearest", "on_duty", "official_visit", "work_from_home", "field_duty"];

    /// <summary>Every key an explanation can hold (the text files must have each, in English and Urdu).</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        ExplainHoliday, ExplainWeeklyOff, ExplainOnLeave, ExplainOnDuty, ExplainAbsentNoPunch, ExplainAbsentWorked, ExplainMissingOut, ExplainMissingIn,
        ExplainUnscheduled, ExplainHalfDayWorked, ExplainHalfDayLate, ExplainHalfDayEarly, ExplainLate, ExplainPresent, ExplainPresentLeftEarly,
        StepShift, StepRosterDay, StepPattern, StepCalendarHoliday, StepSeason, StepPunches, StepNotCounted, StepOutsideWindow, StepBreakFixed, StepBreakPunches, StepRounded, StepWithinGrace,
        StepEarly, StepEarlyWithinGrace, StepFullDay, StepPartLeave, StepOddCount, StepRestWorkRecorded, StepRestWorkIgnored, StepPunchesIgnored, StepPolicy,
        StepDutyPart, StepHourLeave,
    ];

    /// <summary>Local start and end of a shift on a date (a shift that crosses midnight ends the next day; a continuous one lasts 24 hours).</summary>
    public static (DateTime Start, DateTime End) ShiftTimes(DateOnly date, DayShift shift)
    {
        ArgumentNullException.ThrowIfNull(shift);
        var start = date.ToDateTime(shift.Start);
        var span = ShiftRules.SpanMinutes(shift.Start, shift.End, shift.CrossesMidnight);
        if (span <= 0)
        {
            span += ShiftRules.DayMinutes;
        }

        return (start, start.AddMinutes(span));
    }

    /// <summary>
    /// The shift with a season's times (S1.28b <c>seasonal_timing</c>; R-158): start, end and crossing midnight come from the season, and the
    /// shift's own full-day minutes shrink or grow in proportion to the span (a 9-hour shift needing 480 minutes, cut to 6 hours, needs 320). A
    /// policy's <c>fullDayMinutes</c> stays absolute (it is the policy's own rule), and the policy decides how breaks count, as on any day.
    /// </summary>
    public static DayShift WithSeason(DayShift shift, TimeOnly start, TimeOnly end, bool crossesMidnight)
    {
        ArgumentNullException.ThrowIfNull(shift);
        var seasonal = shift with { Start = start, End = end, CrossesMidnight = crossesMidnight };
        var (baseStart, baseEnd) = ShiftTimes(DateOnly.MinValue, shift);
        var (newStart, newEnd) = ShiftTimes(DateOnly.MinValue, seasonal);
        var baseSpan = (long)(baseEnd - baseStart).TotalMinutes;
        var newSpan = (long)(newEnd - newStart).TotalMinutes;
        return baseSpan <= 0 || baseSpan == newSpan
            ? seasonal
            : seasonal with { MinFullDayMinutes = (int)(shift.MinFullDayMinutes * newSpan / baseSpan) };
    }

    /// <summary>
    /// The local time range whose punches belong to <paramref name="date"/> (ruling Q3): from midnight, or from the end of the previous day's
    /// shift plus its window when that reaches past midnight; to midnight, or to the end of this day's shift plus the window when that reaches past
    /// midnight (a night shift, or a 15:00 to 23:00 shift whose window ends at 01:00; review S1.29a finding 3). Consecutive days never overlap.
    /// </summary>
    public static (DateTime From, DateTime To) OwnedRange(DateOnly date, DayShift? shift, int windowMinutes, DayShift? previousShift, int previousWindowMinutes)
    {
        var from = date.ToDateTime(TimeOnly.MinValue);
        if (previousShift is not null)
        {
            var (_, prevEnd) = ShiftTimes(date.AddDays(-1), previousShift);
            var cut = prevEnd.AddMinutes(previousWindowMinutes);
            if (cut > from)
            {
                from = cut;
            }
        }

        var to = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        if (shift is not null)
        {
            var (_, end) = ShiftTimes(date, shift);
            var cut = end.AddMinutes(windowMinutes);
            if (cut > to)
            {
                to = cut;
            }
        }

        return (from, to);
    }

    /// <summary>
    /// The punches that count, in time order: duplicates are left out, and so is any punch a correction replaced (<c>supersedes_event_id</c>,
    /// in <paramref name="supersededIds"/>, which also covers corrections dated on another day).
    /// </summary>
    public static IReadOnlyList<DayPunch> Counted(IEnumerable<DayPunch> punches, IReadOnlySet<Guid> supersededIds) =>
        [.. punches.Where(p => !p.IsDuplicate && !supersededIds.Contains(p.Id)).OrderBy(p => p.At).ThenBy(p => p.Id)];

    /// <summary>Works out the day. <paramref name="input"/>'s punches are the counted ones of the day's owned range.</summary>
    public static DayOutcome Evaluate(DayInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var policy = input.Policy;
        var steps = new List<ExplanationLine>();
        var exceptions = new List<string>();
        var plan = input.Plan;
        var shift = plan.Shift;
        var punches = input.Punches.OrderBy(p => p.At).ThenBy(p => p.Id).ToList();
        string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, input.Zone).ToString("HH:mm", CultureInfo.InvariantCulture);

        if (plan.Source == DayPlan.FromRoster)
        {
            steps.Add(Line(StepRosterDay, ("dayType", Value(plan.DayType))));
        }
        else if (plan.Source == DayPlan.FromPattern)
        {
            steps.Add(Line(StepPattern));
        }
        else if (plan.Source == DayPlan.FromCalendar)
        {
            steps.Add(Line(StepCalendarHoliday));
        }

        if (plan.Season is { } season)
        {
            steps.Add(Line(StepSeason, ("season", season.Name), ("policy", season.PolicyCode)));
        }

        if (shift is not null)
        {
            var (s, e) = ShiftTimes(input.Date, shift);
            steps.Add(Line(StepShift, ("shift", shift.Code), ("start", s.ToString("HH:mm", CultureInfo.InvariantCulture)), ("end", e.ToString("HH:mm", CultureInfo.InvariantCulture))));
        }

        DayOutcome Done(string result, string rule, ExplanationLine summary, DateTimeOffset? firstIn = null, DateTimeOffset? lastOut = null, int worked = 0, int late = 0, int early = 0, Guid? leaveId = null, Guid? dutyId = null)
        {
            steps.Add(Line(StepPolicy, ("policy", input.PolicyRef.Code), ("version", input.PolicyRef.Version)));
            return new DayOutcome(plan.DayType, result, shift?.Id, firstIn, lastOut, worked, late, early, leaveId, dutyId,
                new DayExplanation(rule, summary, steps, input.PolicyRef), exceptions.Distinct(StringComparer.Ordinal).ToList());
        }

        // 1. A holiday or weekly off on the plan: punches only count as recorded work (overtime is S1.29b's).
        if (plan.DayType is DayPlan.Holiday or DayPlan.WeeklyOff)
        {
            var holiday = plan.DayType == DayPlan.Holiday;
            var record = (holiday ? policy.HolidayWork : policy.WeeklyOffWork) == AttendancePolicy.Record;
            DateTimeOffset? firstIn = null, lastOut = null;
            var worked = 0;
            if (punches.Count > 0 && record)
            {
                (firstIn, lastOut, worked) = Pair(punches, policy, steps, exceptions, Local);
                steps.Add(Line(StepRestWorkRecorded, ("worked", worked)));
            }
            else if (punches.Count > 0)
            {
                steps.Add(Line(StepRestWorkIgnored, ("count", punches.Count)));
            }

            return holiday
                ? Done(DayResults.Holiday, "holiday", Line(ExplainHoliday), firstIn, lastOut, worked)
                : Done(DayResults.WeeklyOff, "weekly_off", Line(ExplainWeeklyOff), firstIn, lastOut, worked);
        }

        // 2. Approved leave of a whole day.
        var leaveQuantity = input.Leave.Sum(l => l.Quantity);
        var leaveMinutes = input.Leave.Sum(l => l.Minutes ?? 0);
        var leaveId = input.Leave.OrderBy(l => l.LeaveRequestId).Select(l => (Guid?)l.LeaveRequestId).FirstOrDefault();
        var baseFullDay = shift is null ? (int?)null : policy.FullDayMinutes ?? shift.MinFullDayMinutes;
        if (leaveQuantity >= 1m || (baseFullDay is { } whole && leaveMinutes >= whole))
        {
            if (punches.Count > 0)
            {
                steps.Add(Line(StepPunchesIgnored, ("count", punches.Count)));
            }

            return Done(DayResults.OnLeave, "on_leave", Line(ExplainOnLeave), leaveId: leaveId);
        }

        // 3. Approved duty (on duty, official visit, work from home) covering the whole shift, or any duty on a day without a shift.
        var dutyCovers = input.Duty is { } d && (shift is null || d.Start is null || d.End is null
            || (LocalTime(d.Start.Value, input.Zone) <= ShiftTimes(input.Date, shift).Start && LocalTime(d.End.Value, input.Zone) >= ShiftTimes(input.Date, shift).End));
        if (input.Duty is { } duty && dutyCovers)
        {
            if (punches.Count > 0)
            {
                steps.Add(Line(StepPunchesIgnored, ("count", punches.Count)));
            }

            return Done(DayResults.OnDuty, "on_duty", Line(ExplainOnDuty, ("kind", Value(duty.Kind))), leaveId: leaveId, dutyId: duty.DutyRequestId);
        }

        // 4. Punches on a day nothing planned (the caller does not process an unplanned day without punches).
        if (shift is null)
        {
            exceptions.Add(DayResults.Unscheduled);
            var (fi, lo, w) = punches.Count > 0 ? Pair(punches, policy, steps, exceptions, Local) : (null, null, 0);
            exceptions.Remove(DayResults.MissingOut);
            exceptions.Remove(DayResults.MissingIn);
            return Done(DayResults.Incomplete, "unscheduled", Line(ExplainUnscheduled, ("count", punches.Count)), fi, lo, w, leaveId: leaveId);
        }

        // 5. A working day with a shift: only punches inside the shift's window count.
        var (shiftStart, shiftEnd) = ShiftTimes(input.Date, shift);
        var windowFrom = shiftStart.AddMinutes(-policy.ShiftWindowMinutes);
        var windowTo = shiftEnd.AddMinutes(policy.ShiftWindowMinutes);
        var inside = punches.Where(p => LocalTime(p.At, input.Zone) >= windowFrom && LocalTime(p.At, input.Zone) <= windowTo).ToList();
        if (inside.Count < punches.Count)
        {
            exceptions.Add(DayResults.OutsideTolerance);
            steps.Add(Line(StepOutsideWindow, ("count", punches.Count - inside.Count), ("window", policy.ShiftWindowMinutes)));
        }

        // A duty shorter than the shift counts as time at work: an in and an out punch at its start and end, inside the window.
        if (input.Duty is { Start: { } dutyStart, End: { } dutyEnd } partDuty)
        {
            var zoneOffset = (DateTime t) => new DateTimeOffset(t, input.Zone.GetUtcOffset(t));
            var from = dutyStart > zoneOffset(windowFrom) ? dutyStart : zoneOffset(windowFrom);
            var to = dutyEnd < zoneOffset(windowTo) ? dutyEnd : zoneOffset(windowTo);
            if (from < to)
            {
                inside.Add(new DayPunch(partDuty.DutyRequestId, from, "in"));
                inside.Add(new DayPunch(DutyOutId(partDuty.DutyRequestId), to, "out"));
                inside = [.. inside.OrderBy(p => p.At).ThenBy(p => p.Id)];
                steps.Add(Line(StepDutyPart, ("kind", Value(partDuty.Kind)), ("from", Local(from)), ("to", Local(to))));
            }
        }

        var startText = shiftStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        var endText = shiftEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (inside.Count == 0)
        {
            return Done(DayResults.Absent, "absent_no_punch", Line(ExplainAbsentNoPunch, ("start", startText), ("end", endText)), leaveId: leaveId);
        }

        if (inside.Count == 1)
        {
            var only = inside[0];
            var missingIn = only.Direction == "out";
            exceptions.Add(missingIn ? DayResults.MissingIn : DayResults.MissingOut);
            steps.Add(Line(StepPunches, ("count", 1), ("firstIn", Local(only.At)), ("lastOut", Local(only.At))));
            return missingIn
                ? Done(DayResults.Incomplete, "missing_in", Line(ExplainMissingIn, ("at", Local(only.At))), lastOut: only.At, leaveId: leaveId)
                : Done(DayResults.Incomplete, "missing_out", Line(ExplainMissingOut, ("at", Local(only.At))), firstIn: only.At, leaveId: leaveId);
        }

        var (first, last, workedMinutes) = Pair(inside, policy, steps, exceptions, Local);

        // Part-day leave (a half day): the leave's half is not checked for lateness or early leaving, and the minutes needed shrink by its share.
        var part = input.Leave.Where(l => l.Quantity > 0m).ToList();
        var share = leaveQuantity > 0m ? 1m - leaveQuantity : 1m;
        var firstHalfOff = part.Exists(l => l.Part == DayLeave.FirstHalf);
        var secondHalfOff = part.Exists(l => l.Part == DayLeave.SecondHalf);
        if (part.Count > 0)
        {
            steps.Add(Line(StepPartLeave, ("quantity", leaveQuantity), ("part", Value(firstHalfOff ? DayLeave.FirstHalf : secondHalfOff ? DayLeave.SecondHalf : DayLeave.Full))));
        }

        if (leaveMinutes > 0)
        {
            steps.Add(Line(StepHourLeave, ("minutes", leaveMinutes)));
        }

        var fullDaySource = policy.FullDayMinutes is null ? "shift" : "policy";
        var fullDay = Math.Max(0, (int)Math.Floor((policy.FullDayMinutes ?? shift.MinFullDayMinutes) * share) - leaveMinutes);
        var absentBelow = Math.Max(0, (int)Math.Floor(policy.AbsentBelowMinutes * share) - leaveMinutes);

        var lateReference = Reference(input.Date, shift, shift.ShiftType == "flexible" ? shift.CoreStart : null, shiftStart);
        var earlyReference = Reference(input.Date, shift, shift.ShiftType == "flexible" ? shift.CoreEnd : null, shiftEnd);
        var lateRaw = firstHalfOff ? 0 : Math.Max(0, WholeMinutes(LocalTime(first!.Value, input.Zone) - lateReference));
        var earlyRaw = secondHalfOff ? 0 : Math.Max(0, WholeMinutes(earlyReference - LocalTime(last!.Value, input.Zone)));

        // Hourly leave says how long, not when: its minutes excuse lateness first, then early leaving (CR-089 S1.29a detail n).
        var excuse = leaveMinutes;
        var excusedLate = Math.Min(lateRaw, excuse);
        lateRaw -= excusedLate;
        earlyRaw -= Math.Min(earlyRaw, excuse - excusedLate);
        var late = lateRaw > policy.LateGraceMinutes ? lateRaw : 0;
        var early = earlyRaw > policy.EarlyGraceMinutes ? earlyRaw : 0;
        var refStart = lateReference.ToString("HH:mm", CultureInfo.InvariantCulture);
        var refEnd = earlyReference.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (lateRaw > 0 && late == 0)
        {
            steps.Add(Line(StepWithinGrace, ("late", lateRaw), ("grace", policy.LateGraceMinutes)));
        }

        if (earlyRaw > 0 && early == 0)
        {
            steps.Add(Line(StepEarlyWithinGrace, ("early", earlyRaw), ("grace", policy.EarlyGraceMinutes)));
        }
        else if (early > 0)
        {
            steps.Add(Line(StepEarly, ("early", early), ("end", refEnd), ("lastOut", Local(last!.Value)), ("grace", policy.EarlyGraceMinutes)));
        }

        steps.Add(Line(StepFullDay, ("minutes", fullDay), ("source", Value(fullDaySource)), ("absentBelow", absentBelow)));
        DayOutcome Worked(string result, string rule, ExplanationLine summary) => Done(result, rule, summary, first, last, workedMinutes, late, early, leaveId, input.Duty?.DutyRequestId);

        if (workedMinutes < absentBelow)
        {
            return Worked(DayResults.Absent, "absent_worked", Line(ExplainAbsentWorked, ("worked", workedMinutes), ("limit", absentBelow)));
        }

        if (policy.LateHalfDayAfterMinutes is { } lateHalf && late >= lateHalf)
        {
            return Worked(DayResults.HalfDay, "half_day_late", Line(ExplainHalfDayLate, ("late", late), ("limit", lateHalf), ("start", refStart), ("firstIn", Local(first!.Value))));
        }

        if (policy.EarlyHalfDayAfterMinutes is { } earlyHalf && early >= earlyHalf)
        {
            return Worked(DayResults.HalfDay, "half_day_early", Line(ExplainHalfDayEarly, ("early", early), ("limit", earlyHalf), ("end", refEnd), ("lastOut", Local(last!.Value))));
        }

        if (workedMinutes < fullDay)
        {
            return Worked(DayResults.HalfDay, "half_day_worked", Line(ExplainHalfDayWorked, ("worked", workedMinutes), ("limit", fullDay)));
        }

        if (late > 0)
        {
            return Worked(DayResults.Late, "late", Line(ExplainLate, ("late", late), ("start", refStart), ("firstIn", Local(first!.Value)), ("grace", policy.LateGraceMinutes)));
        }

        return early > 0
            ? Worked(DayResults.Present, "present_left_early", Line(ExplainPresentLeftEarly, ("early", early), ("end", refEnd), ("lastOut", Local(last!.Value)), ("grace", policy.EarlyGraceMinutes)))
            : Worked(DayResults.Present, "present", Line(ExplainPresent, ("firstIn", Local(first!.Value)), ("lastOut", Local(last!.Value)), ("worked", workedMinutes)));
    }

    /// <summary>First in, last out and worked minutes (breaks by the policy, then rounding); adds the odd-count exception and the steps.</summary>
    private static (DateTimeOffset? First, DateTimeOffset? Last, int Worked) Pair(
        List<DayPunch> punches, AttendancePolicy policy, List<ExplanationLine> steps, List<string> exceptions, Func<DateTimeOffset, string> local)
    {
        var first = punches[0].At;
        var last = punches[^1].At;
        steps.Add(Line(StepPunches, ("count", punches.Count), ("firstIn", local(first)), ("lastOut", local(last))));
        if (punches.Count == 1)
        {
            exceptions.Add(punches[0].Direction == "out" ? DayResults.MissingIn : DayResults.MissingOut);
            return (punches[0].Direction == "out" ? null : first, punches[0].Direction == "out" ? last : null, 0);
        }

        if (punches.Count >= 3 && punches.Count % 2 == 1)
        {
            exceptions.Add(DayResults.OddCount);
            steps.Add(Line(StepOddCount, ("count", punches.Count)));
        }

        var span = WholeMinutes(last - first);
        var worked = span;
        if (policy.BreakMode == AttendancePolicy.BreakFixed && policy.BreakMinutes is { } fixedBreak)
        {
            worked = Math.Max(0, span - fixedBreak);
            steps.Add(Line(StepBreakFixed, ("minutes", fixedBreak)));
        }
        else if (policy.BreakMode == AttendancePolicy.BreakByPunches && punches.Count >= 4)
        {
            // Gaps between the 2nd and 3rd, 4th and 5th, ... punches are breaks; a last middle punch without a partner gives none.
            var breaks = 0;
            for (var i = 1; i + 1 <= punches.Count - 2; i += 2)
            {
                breaks += WholeMinutes(punches[i + 1].At - punches[i].At);
            }

            worked = Math.Max(0, span - breaks);
            steps.Add(Line(StepBreakPunches, ("minutes", breaks)));
        }

        var rounded = policy.Round(worked);
        if (rounded != worked)
        {
            steps.Add(Line(StepRounded, ("mode", Value(policy.RoundingMode)), ("step", policy.RoundingStepMinutes ?? 0), ("from", worked), ("to", rounded)));
        }

        return (first, last, rounded);
    }

    /// <summary>A core time of a flexible shift on the day (after midnight for a shift that crosses it), or the shift's own start or end.</summary>
    private static DateTime Reference(DateOnly date, DayShift shift, TimeOnly? core, DateTime fallback)
    {
        if (core is not { } c)
        {
            return fallback;
        }

        var at = date.ToDateTime(c);
        return shift.CrossesMidnight && c < shift.Start ? at.AddDays(1) : at;
    }

    private static DateTime LocalTime(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).DateTime;

    /// <summary>A coded value as the key of its text.</summary>
    private static string Value(string code) => ValuePrefix + code;

    /// <summary>A stable id for the out punch a part-day duty stands for (the same duty always gives the same id, so results repeat).</summary>
    private static Guid DutyOutId(Guid dutyId)
    {
        var bytes = dutyId.ToByteArray();
        bytes[0] ^= 0xFF;
        return new Guid(bytes);
    }

    /// <summary>Whole minutes of a time span, cut towards zero (09:10:59 after 09:00 is 10 minutes).</summary>
    public static int WholeMinutes(TimeSpan span) => (int)Math.Truncate(span.TotalMinutes);

    private static ExplanationLine Line(string key, params (string Name, object Value)[] values) =>
        new(key, values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));

    /// <summary>The stored explanation's text in a language: each <c>{name}</c> of the template is replaced by its value (unit tests, reports).</summary>
    public static string Render(JsonElement explanation, IReadOnlyDictionary<string, string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        static string One(JsonElement line, IReadOnlyDictionary<string, string> texts)
        {
            var template = texts[line.GetProperty("key").GetString()!];
            if (line.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ps.EnumerateObject())
                {
                    var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                    if (value.StartsWith(ValuePrefix, StringComparison.Ordinal) && texts.TryGetValue(value, out var text))
                    {
                        value = text;
                    }

                    template = template.Replace("{" + p.Name + "}", value, StringComparison.Ordinal);
                }
            }

            return template;
        }

        var parts = new List<string> { One(explanation, texts) };
        if (explanation.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            parts.AddRange(steps.EnumerateArray().Select(s => One(s, texts)));
        }

        return string.Join(" ", parts);
    }
}
