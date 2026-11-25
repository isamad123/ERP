namespace Erp.Hrm.Application.Leave;

/// <summary>The part of a day a request starts or ends with (FR-HRM-LVE-04).</summary>
public static class LeaveParts
{
    public const string Full = "full";
    public const string FirstHalf = "first_half";
    public const string SecondHalf = "second_half";

    public static IReadOnlyList<string> All { get; } = [Full, FirstHalf, SecondHalf];
}

/// <summary>What a day of the employee's plan is (from the effective shift of S1.28b): <c>working</c>, <c>weekly_off</c>, <c>holiday</c> or <c>none</c> (nothing says).</summary>
public static class PlanDayTypes
{
    public const string Working = "working";
    public const string WeeklyOff = "weekly_off";
    public const string Holiday = "holiday";
    public const string None = "none";
}

/// <summary>
/// One day of the employee's plan as the day counting needs it: the kind of day and, for hourly leave, the hours of work on it (the shift's
/// time less its break, or the setting <c>hrm.leave.hours_per_day</c> when no shift names the day; null when neither gives a number).
/// </summary>
public sealed record PlanDay(DateOnly Date, string DayType, decimal? WorkHours);

/// <summary>
/// One day of a request as the day list shows it (slice S1.32 Build items 7 and 8): the quantity it takes (in the type's unit: days, or hours for
/// a type counted in hours), how it counts (<c>leave</c>; <c>sandwich</c>: an off day between two leave days counted as leave; <c>off</c>: not
/// counted) with the reason as a text key, and the part of the day.
/// </summary>
public sealed record LeaveDay(DateOnly Date, string DayType, decimal Quantity, string Kind, string Part, string Reason);

/// <summary>The days a request takes, its total in the type's unit, and the refusal (a text key with the field it concerns) when it cannot be counted.</summary>
public sealed record LeaveCount(IReadOnlyList<LeaveDay> Days, decimal Total, string? Field = null, string? Error = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Counting the days of a leave request (slice S1.32 Build items 7 and 8; FR-HRM-LVE-01, -03, -04; rulings Q4 and Q5). Pure: the plan of each
/// day comes from the caller. A type counted in calendar days takes every day; one counted in working days takes working days (and days nothing
/// plans, which the day list marks), and, when the policy's sandwich mode says so, holidays and weekly-off days that lie between two leave days of
/// the same request; leading and trailing off days never count. Half days are allowed only for a type taken in half days: the first day may
/// start with its second half, the last may end with its first half, a single day may be either half. A type counted in hours takes one day and
/// at most that day's working hours.
/// </summary>
public static class LeaveDayRules
{
    public const string Sandwich = "sandwich";
    public const string Leave = "leave";
    public const string Off = "off";

    public const string DatesRequired = "errors.hrm.leave.request.dates";
    public const string DateOrder = "errors.hrm.leave.request.date_order";
    public const string TooLong = "errors.hrm.leave.request.too_long";
    public const string PartInvalid = "errors.hrm.leave.request.part";
    public const string HalfNotAllowed = "errors.hrm.leave.request.half_not_allowed";
    public const string HoursOneDay = "errors.hrm.leave.request.hours_one_day";
    public const string HoursRequired = "errors.hrm.leave.request.hours_required";
    public const string HoursNotUsed = "errors.hrm.leave.request.hours_not_used";
    public const string HoursUnknown = "errors.hrm.leave.request.hours_unknown";
    public const string HoursTooMany = "errors.hrm.leave.request.hours_too_many";
    public const string NotWorkingDay = "errors.hrm.leave.request.not_working_day";
    public const string NoDays = "errors.hrm.leave.request.no_days";

    /// <summary>The longest request, in calendar days (a request stays within one leave year anyway).</summary>
    public const int MostDays = 366;

    public static LeaveCount Count(string counting, string unit, DateOnly start, DateOnly end, string startPart, string endPart, decimal? hours, string sandwich, IReadOnlyList<PlanDay> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (end < start)
        {
            return Fail("endDate", DateOrder);
        }

        if (end.DayNumber - start.DayNumber + 1 > MostDays)
        {
            return Fail("endDate", TooLong);
        }

        if (!LeaveParts.All.Contains(startPart, StringComparer.Ordinal))
        {
            return Fail("startPart", PartInvalid);
        }

        if (!LeaveParts.All.Contains(endPart, StringComparer.Ordinal))
        {
            return Fail("endPart", PartInvalid);
        }

        var byDate = plan.ToDictionary(p => p.Date);
        PlanDay At(DateOnly d) => byDate.TryGetValue(d, out var p) ? p : new PlanDay(d, PlanDayTypes.None, null);

        if (counting == "hours")
        {
            if (start != end)
            {
                return Fail("endDate", HoursOneDay);
            }

            if (startPart != LeaveParts.Full || endPart != LeaveParts.Full)
            {
                return Fail("startPart", PartInvalid);
            }

            if (hours is not { } h || h <= 0m || decimal.Round(h, 2) != h)
            {
                return Fail("hours", HoursRequired);
            }

            var day = At(start);
            if (day.DayType is PlanDayTypes.WeeklyOff or PlanDayTypes.Holiday)
            {
                return Fail("startDate", NotWorkingDay);
            }

            if (day.WorkHours is not { } work || work <= 0m)
            {
                return Fail("hours", HoursUnknown);
            }

            if (h > work)
            {
                return Fail("hours", HoursTooMany);
            }

            return new LeaveCount([new LeaveDay(start, day.DayType, h, Leave, LeaveParts.Full, ReasonOf(day.DayType, counted: true))], h);
        }

        if (hours is not null)
        {
            return Fail("hours", HoursNotUsed);
        }

        // Parts: one day takes either half (the end part repeats the start part, or is full); a longer request starts with the whole day or its
        // second half and ends with the whole day or its first half. Half days only for a type taken in half days.
        if (start == end)
        {
            if (endPart != LeaveParts.Full && endPart != startPart)
            {
                return Fail("endPart", PartInvalid);
            }
        }
        else
        {
            if (startPart == LeaveParts.FirstHalf)
            {
                return Fail("startPart", PartInvalid);
            }

            if (endPart == LeaveParts.SecondHalf)
            {
                return Fail("endPart", PartInvalid);
            }
        }

        if ((startPart != LeaveParts.Full || endPart != LeaveParts.Full) && unit != "half_day")
        {
            return Fail(startPart != LeaveParts.Full ? "startPart" : "endPart", HalfNotAllowed);
        }

        var calendar = counting == "calendar_days";
        var dates = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i)).ToList();
        bool Counts(PlanDay d) => calendar || d.DayType is PlanDayTypes.Working or PlanDayTypes.None;
        var counted = dates.Where(d => Counts(At(d))).ToList();
        var firstLeave = counted.Count > 0 ? counted[0] : (DateOnly?)null;
        var lastLeave = counted.Count > 0 ? counted[^1] : (DateOnly?)null;
        var days = new List<LeaveDay>(dates.Count);
        foreach (var date in dates)
        {
            var day = At(date);
            var part = date == start && start == end ? startPart : date == start ? startPart : date == end ? endPart : LeaveParts.Full;
            var share = part == LeaveParts.Full ? 1m : 0.5m;
            if (Counts(day))
            {
                days.Add(new LeaveDay(date, day.DayType, share, Leave, part, ReasonOf(day.DayType, counted: true, calendar)));
            }
            else if (firstLeave is { } f && lastLeave is { } l && date > f && date < l && SandwichCounts(sandwich, day.DayType))
            {
                days.Add(new LeaveDay(date, day.DayType, 1m, Sandwich, LeaveParts.Full, "hrm.leave.day.sandwich_" + day.DayType));
            }
            else
            {
                days.Add(new LeaveDay(date, day.DayType, 0m, Off, LeaveParts.Full, "hrm.leave.day.off_" + day.DayType));
            }
        }

        var total = days.Sum(d => d.Quantity);
        return total <= 0m ? new LeaveCount(days, 0m, "startDate", NoDays) : new LeaveCount(days, total);
    }

    /// <summary>Whether the sandwich mode (<c>off</c>, <c>holidays</c>, <c>weekly_off</c>, <c>both</c>) counts an off day of this kind.</summary>
    public static bool SandwichCounts(string mode, string dayType) => (mode, dayType) switch
    {
        ("holidays" or "both", PlanDayTypes.Holiday) => true,
        ("weekly_off" or "both", PlanDayTypes.WeeklyOff) => true,
        _ => false,
    };

    private static string ReasonOf(string dayType, bool counted, bool calendar = false) =>
        dayType == PlanDayTypes.None && counted && !calendar ? "hrm.leave.day.leave_unplanned"
        : calendar && dayType is PlanDayTypes.Holiday or PlanDayTypes.WeeklyOff ? "hrm.leave.day.calendar_" + dayType
        : "hrm.leave.day.leave";

    private static LeaveCount Fail(string field, string error) => new([], 0m, field, error);
}

/// <summary>
/// The checks of a request against the rules of its type in the employee's <c>leave</c> policy (FR-HRM-LVE-03; slice S1.32 Build item 7). Pure;
/// the caller gives the facts. Each answer is a text key, or null when the rule allows the request (a rule the policy does not set allows it).
/// </summary>
public static class LeaveRequestChecks
{
    public const string Gender = "errors.hrm.leave.request.gender";
    public const string Tenure = "errors.hrm.leave.request.tenure";
    public const string Probation = "errors.hrm.leave.request.probation";
    public const string Notice = "errors.hrm.leave.request.notice";
    public const string Longest = "errors.hrm.leave.request.longest";
    public const string Combined = "errors.hrm.leave.request.combined";

    /// <summary>Gender, months of service on the first day and probation (an employee on probation on the first day when <c>duringProbation</c> is false).</summary>
    public static string? Eligibility(LeaveTypeRules rules, string? gender, int serviceMonths, bool onProbation)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Genders is { Count: > 0 } genders && !genders.Contains(gender ?? "unspecified", StringComparer.Ordinal))
        {
            return Gender;
        }

        if (rules.MinServiceMonths is { } months && serviceMonths < months)
        {
            return Tenure;
        }

        return rules.DuringProbation == false && onProbation ? Probation : null;
    }

    /// <summary>The minimum notice in calendar days between the day the request is made and its first day (leave asked for afterwards has none).</summary>
    public static string? NoticeCheck(LeaveTypeRules rules, DateOnly today, DateOnly start)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.MinNoticeDays is { } notice && start.DayNumber - today.DayNumber < notice ? Notice : null;
    }

    /// <summary>The longest leave of the type, counted as the request counts (sandwich days included).</summary>
    public static string? LongestCheck(LeaveTypeRules rules, decimal total)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.MaxConsecutiveDays is { } most && total > most ? Longest : null;
    }

    /// <summary>
    /// Combining: a leave that touches another (no working day between them) may not be of a type either one's rules list in <c>notCombinedWith</c>.
    /// <paramref name="neighbours"/> are the codes of the touching leave and their own lists.
    /// </summary>
    public static string? CombinedCheck(LeaveTypeRules rules, IEnumerable<(string Code, IReadOnlyList<string> NotCombinedWith)> neighbours)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(neighbours);
        return neighbours.Any(n => rules.NotCombinedWith.Contains(n.Code, StringComparer.OrdinalIgnoreCase) || n.NotCombinedWith.Contains(rules.LeaveType, StringComparer.OrdinalIgnoreCase))
            ? Combined : null;
    }
}
