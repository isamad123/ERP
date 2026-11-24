namespace Erp.Hrm.Application.Attendance;

/// <summary>One day of a roster as the checks see it.</summary>
public sealed record RosterDay(Guid EmploymentId, DateOnly Date, string DayType, Guid? ShiftId, string? Reason);

/// <summary>A shift's times for the rest-hour check.</summary>
public sealed record ShiftTimes(TimeOnly Start, TimeOnly End, bool CrossesMidnight);

/// <summary>A day of another roster of the same employee (a draft or published roster covering the same dates).</summary>
public sealed record OtherRosterDay(Guid RosterId, Guid EmploymentId, DateOnly Date, string DayType, Guid? ShiftId);

/// <summary>An employment's first and last day, for the "shift outside employment dates" check.</summary>
public sealed record EmploymentSpan(DateOnly From, DateOnly? To);

/// <summary>One finding of the roster checks. <see cref="Severity"/> is <c>error</c> (publishing is refused) or <c>warning</c>.</summary>
public sealed record RosterCheckMessage(string Code, string Severity, Guid EmploymentId, DateOnly Date, IReadOnlyDictionary<string, object?>? Detail = null);

/// <summary>
/// The checks of a roster, returned together (slice S1.28a Build item 4, FR-HRM-ATT-07): an employee with two shifts on one day (another roster
/// also has them working that day), rest between two working days below the shift policy's minimum (only when the customer set one; no
/// default, AS-4), a day outside the employment's dates, and work on a rest day without a reason. The first two kinds of problem that make a
/// roster impossible (two shifts, outside the employment) are errors; the others are warnings the planner may accept.
/// </summary>
public static class RosterChecks
{
    public const string DoubleAssignment = "double_assignment";
    public const string ShortRest = "short_rest";
    public const string OutsideEmployment = "outside_employment";
    public const string RestDayWork = "rest_day_work";
    public const string Error = "error";
    public const string Warning = "warning";

    /// <param name="days">The roster's own days.</param>
    /// <param name="others">Days of the same employees in other rosters over the same dates (and the days around the period).</param>
    /// <param name="shifts">Times of every shift named by either set.</param>
    /// <param name="minimumRest">Minutes of rest the shift policy asks for an employment on a date; null means no minimum is set and nothing is checked.</param>
    /// <param name="spans">First and last day of each employment.</param>
    /// <param name="isRestDay">True when the employee's pattern makes the date a weekly off.</param>
    public static IReadOnlyList<RosterCheckMessage> Run(
        IReadOnlyCollection<RosterDay> days,
        IReadOnlyCollection<OtherRosterDay> others,
        IReadOnlyDictionary<Guid, ShiftTimes> shifts,
        Func<Guid, DateOnly, int?> minimumRest,
        IReadOnlyDictionary<Guid, EmploymentSpan> spans,
        Func<Guid, DateOnly, bool> isRestDay)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(shifts);
        ArgumentNullException.ThrowIfNull(minimumRest);
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(isRestDay);
        var result = new List<RosterCheckMessage>();
        var own = days.ToDictionary(d => (d.EmploymentId, d.Date));
        var elsewhere = others.Where(o => o.DayType == "working").ToLookup(o => (o.EmploymentId, o.Date));

        foreach (var d in days.OrderBy(d => d.EmploymentId).ThenBy(d => d.Date))
        {
            if (spans.TryGetValue(d.EmploymentId, out var span) && (d.Date < span.From || span.To is { } to && d.Date > to))
            {
                result.Add(new RosterCheckMessage(OutsideEmployment, Error, d.EmploymentId, d.Date));
            }

            if (d.DayType != "working")
            {
                continue;
            }

            if (elsewhere[(d.EmploymentId, d.Date)].FirstOrDefault() is { } clash)
            {
                result.Add(new RosterCheckMessage(DoubleAssignment, Error, d.EmploymentId, d.Date, new Dictionary<string, object?> { ["otherRosterId"] = clash.RosterId }));
            }

            if (isRestDay(d.EmploymentId, d.Date) && string.IsNullOrWhiteSpace(d.Reason))
            {
                result.Add(new RosterCheckMessage(RestDayWork, Warning, d.EmploymentId, d.Date));
            }

            if (minimumRest(d.EmploymentId, d.Date) is { } minimum && d.ShiftId is { } shift && shifts.TryGetValue(shift, out var today))
            {
                var next = d.Date.AddDays(1);
                var nextShift = own.TryGetValue((d.EmploymentId, next), out var nd) && nd.DayType == "working" ? nd.ShiftId
                    : elsewhere[(d.EmploymentId, next)].Select(o => o.ShiftId).FirstOrDefault();
                if (nextShift is { } ns && shifts.TryGetValue(ns, out var tomorrow))
                {
                    var rest = RestMinutes(today, tomorrow);
                    if (rest < minimum)
                    {
                        result.Add(new RosterCheckMessage(ShortRest, Warning, d.EmploymentId, next, new Dictionary<string, object?> { ["restMinutes"] = rest, ["minimumMinutes"] = minimum }));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Minutes between the end of a shift and the start of the shift on the next calendar day.</summary>
    public static int RestMinutes(ShiftTimes today, ShiftTimes tomorrow)
    {
        var end = today.End.Hour * 60 + today.End.Minute + (today.CrossesMidnight || today.End == today.Start ? ShiftRules.DayMinutes : 0);
        var start = ShiftRules.DayMinutes + tomorrow.Start.Hour * 60 + tomorrow.Start.Minute;
        return start - end;
    }
}
