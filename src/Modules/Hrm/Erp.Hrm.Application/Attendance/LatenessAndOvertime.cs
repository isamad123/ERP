namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// One current day result as the lateness rules read it (slice S1.29 Build item 7): the row, its attendance period and policy version, the
/// result with its late and early minutes, and the late and early rules of the policy version that decided the day.
/// </summary>
public sealed record PenaltyDay(DateOnly Date, Guid DayId, Guid PeriodId, Guid PolicyVersionId, string Result, int LateMinutes, int EarlyMinutes, PenaltyRule? Late, PenaltyRule? Early);

/// <summary>A penalty the rules say a day must carry: its kind, the minutes, which late (or early) day of the month it is, the allowance and the rule.</summary>
public sealed record PlannedPenalty(PenaltyDay Day, string Kind, int Minutes, int Occurrence, int Allowed, PenaltyRule Rule);

/// <summary>What an approval of overtime may approve (caps of the day type, FR-HRM-ATT-12): the minutes, and the cap that cut them (null when none did).</summary>
public sealed record OvertimeApproval(int Minutes, string? CappedBy);

/// <summary>
/// The pure rules of slice S1.29b (Build items 7, 9 and 10; FR-HRM-ATT-12, ATT-13, LVE-08). No number of their own: every allowance, cap, multiplier
/// and conversion comes from the customer's <see cref="AttendancePolicy"/> (AS-4, R-002).
/// </summary>
public static class LatenessAndOvertime
{
    public const string Late = "late";
    public const string Early = "early";

    public const string CappedDaily = "daily";
    public const string CappedMonthly = "monthly";
    public const string CappedRequested = "requested";

    // Explanation keys of a penalty (stored as keys and values, translated at display; ruling Q7).
    public const string PenaltyLeave = "hrm.att.penalty.leave";
    public const string PenaltyPay = "hrm.att.penalty.pay_deduction";
    public const string PenaltyWarning = "hrm.att.penalty.warning";
    public const string PenaltyNoLeaveType = "hrm.att.penalty.leave_type_missing";
    public const string PenaltyYearClosed = "hrm.att.penalty.leave_year_closed";

    // Explanation steps of a day's overtime.
    public const string StepOvertimeWorking = "hrm.att.step.overtime_working";
    public const string StepOvertimeRest = "hrm.att.step.overtime_rest";
    public const string StepOvertimeBelowMinimum = "hrm.att.step.overtime_below_minimum";

    /// <summary>Every key of this part (the text files must have each, in English and Urdu).</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [PenaltyLeave, PenaltyPay, PenaltyWarning, PenaltyNoLeaveType, PenaltyYearClosed, StepOvertimeWorking, StepOvertimeRest, StepOvertimeBelowMinimum];

    /// <summary>
    /// The penalties of one employment's calendar month (Build item 7, recorded ruling CR-089 S1.29b detail): the late days are the days whose current
    /// result is <c>late</c> (lateness beyond grace that did not make the day a half day or an absence, which are their own consequence); the early days
    /// are present or late days with early leaving beyond grace. They are counted in date order, each kind on its own; a day whose count is above the
    /// allowance of its own policy version carries a penalty. A waived penalty still counts (the lateness happened; only its effect is removed).
    /// </summary>
    public static IReadOnlyList<PlannedPenalty> Plan(IEnumerable<PenaltyDay> monthDays)
    {
        ArgumentNullException.ThrowIfNull(monthDays);
        var days = monthDays.OrderBy(d => d.Date).ToList();
        var planned = new List<PlannedPenalty>();
        var lateCount = 0;
        var earlyCount = 0;
        foreach (var d in days)
        {
            if (d.Result == DayResults.Late && d.LateMinutes > 0)
            {
                lateCount++;
                if (d.Late is { } rule && lateCount > rule.AllowedPerMonth)
                {
                    planned.Add(new PlannedPenalty(d, Late, d.LateMinutes, lateCount, rule.AllowedPerMonth, rule));
                }
            }

            if (d.Result is DayResults.Present or DayResults.Late && d.EarlyMinutes > 0)
            {
                earlyCount++;
                if (d.Early is { } rule && earlyCount > rule.AllowedPerMonth)
                {
                    planned.Add(new PlannedPenalty(d, Early, d.EarlyMinutes, earlyCount, rule.AllowedPerMonth, rule));
                }
            }
        }

        return planned;
    }

    /// <summary>The first and last day of the calendar month of a date (the month of the allowance and of the monthly cap).</summary>
    public static (DateOnly First, DateOnly Last) Month(DateOnly date)
    {
        var first = new DateOnly(date.Year, date.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>
    /// The overtime of a worked-out day (Build item 9): on a working day with a shift, the minutes after the shift's end (and, when the rule says
    /// so, before its start), never more than the minutes worked, for a present or late day; on a weekly off or holiday, all recorded work. Fewer
    /// minutes than the rule's minimum are not overtime. No rule for the day type: none. Returns the minutes and the explanation step.
    /// </summary>
    public static (int Minutes, ExplanationLine? Step) OvertimeOf(DayInput input, DayOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outcome);
        if (input.Policy.OvertimeFor(outcome.DayType) is not { } rule || outcome.WorkedMinutes <= 0 || outcome.FirstIn is not { } firstIn || outcome.LastOut is not { } lastOut)
        {
            return (0, null);
        }

        int minutes;
        ExplanationLine step;
        if (outcome.DayType == DayPlan.Working)
        {
            if (outcome.Result is not (DayResults.Present or DayResults.Late) || input.Plan.Shift is not { } shift)
            {
                return (0, null);
            }

            var (start, end) = DayRules.ShiftTimes(input.Date, shift);
            var after = Math.Max(0, DayRules.WholeMinutes(TimeZoneInfo.ConvertTime(lastOut, input.Zone).DateTime - end));
            var before = rule.CountBeforeShift ? Math.Max(0, DayRules.WholeMinutes(start - TimeZoneInfo.ConvertTime(firstIn, input.Zone).DateTime)) : 0;
            minutes = Math.Min(after + before, outcome.WorkedMinutes);
            step = Line(StepOvertimeWorking, ("minutes", minutes), ("after", after), ("before", before), ("end", end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)), ("multiplier", rule.Multiplier));
        }
        else
        {
            minutes = outcome.WorkedMinutes;
            step = Line(StepOvertimeRest, ("minutes", minutes), ("dayType", DayRules.ValuePrefix + outcome.DayType), ("multiplier", rule.Multiplier));
        }

        if (minutes <= 0)
        {
            return (0, null);
        }

        return rule.MinimumMinutes is { } minimum && minutes < minimum
            ? (0, Line(StepOvertimeBelowMinimum, ("minutes", minutes), ("minimum", minimum)))
            : (minutes, step);
    }

    /// <summary>
    /// The minutes an approval may approve (Build item 9, caps enforced on approval): the minutes asked for before the day (pre-approval) or worked
    /// (post-approval), within the daily cap and what the monthly cap of the day type leaves after the minutes already approved in the month.
    /// </summary>
    public static OvertimeApproval Approvable(int minutes, int? requestedMinutes, OvertimeRule rule, int approvedThisMonth)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var asked = requestedMinutes ?? minutes;
        var allowed = asked;
        string? capped = null;
        if (rule.DailyCapMinutes is { } daily && daily < allowed)
        {
            allowed = daily;
            capped = CappedDaily;
        }

        if (rule.MonthlyCapMinutes is { } monthly && monthly - approvedThisMonth < allowed)
        {
            allowed = Math.Max(0, monthly - approvedThisMonth);
            capped = CappedMonthly;
        }

        return new OvertimeApproval(Math.Max(0, allowed), capped);
    }

    /// <summary>The days of comp-off approved overtime earns (LVE-08): its minutes over the policy's minutes for one day, to four places (the leave type's unit rounds down).</summary>
    public static decimal CompOffDays(int approvedMinutes, OvertimeRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.CompOffDayMinutes is { } perDay && perDay > 0 ? decimal.Round((decimal)approvedMinutes / perDay, 4, MidpointRounding.ToZero) : 0m;
    }

    /// <summary>The explanation of a penalty: the action's key with the kind, minutes, occurrence, allowance and, for leave, the type and units.</summary>
    public static ExplanationLine Explain(PlannedPenalty p, string? problemKey = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        var key = problemKey ?? p.Rule.Action switch
        {
            PenaltyRule.Leave => PenaltyLeave,
            PenaltyRule.PayDeduction => PenaltyPay,
            _ => PenaltyWarning,
        };
        var values = new List<(string, object)> { ("kind", DayRules.ValuePrefix + p.Kind), ("minutes", p.Minutes), ("occurrence", p.Occurrence), ("allowed", p.Allowed) };
        if (p.Rule.Action == PenaltyRule.Leave)
        {
            values.Add(("leaveType", p.Rule.LeaveTypeCode ?? string.Empty));
            values.Add(("units", p.Rule.LeaveUnits ?? 0m));
        }

        return Line(key, [.. values]);
    }

    private static ExplanationLine Line(string key, params (string Name, object Value)[] values) =>
        new(key, values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));
}
