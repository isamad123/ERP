using System.Globalization;
using System.Text.Json;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// A weekly-off pattern stored in the parameters of the <c>shift</c> policy (slice S1.28a Build item 2, ruling Q2; FR-HRM-ATT-06). The
/// pattern is a small piece of JSON expanded by this code, no table: <c>fixed</c> (the same weekdays every week), <c>alternating_weeks</c>
/// (one set of weekdays in one week, another in the next, counted from a week named by the anchor date) and <c>rotating</c> (a cycle of N
/// days counted from the anchor date, with some positions off). Weekdays are ISO numbers, Monday 1 to Sunday 7. The pattern holds the
/// customer's own days and no statutory number (AS-4).
/// </summary>
public sealed record WeeklyOffPattern(string Kind, IReadOnlyList<int> OffDays, IReadOnlyList<int> AltOffDays, int CycleDays, DateOnly? Anchor)
{
    public const string Fixed = "fixed";
    public const string Alternating = "alternating_weeks";
    public const string Rotating = "rotating";

    public const string KindInvalid = "errors.hrm.shift_pattern.kind_invalid";
    public const string DaysInvalid = "errors.hrm.shift_pattern.days_invalid";
    public const string CycleInvalid = "errors.hrm.shift_pattern.cycle_invalid";
    public const string AnchorRequired = "errors.hrm.shift_pattern.anchor_required";

    /// <summary>Longest period a pattern is expanded for in one call.</summary>
    public const int MaxDays = 370;

    /// <summary>Reads the policy parameters; an error key when the pattern is incomplete (the policy schema checks only the shape).</summary>
    public static (WeeklyOffPattern? Pattern, string? Error) Parse(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("weeklyOffKind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            return (null, KindInvalid);
        }

        var kind = kindElement.GetString()!;
        var off = Ints(parameters, "offDays");
        var alt = Ints(parameters, "altOffDays");
        var cycle = parameters.TryGetProperty("cycleDays", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        DateOnly? anchor = parameters.TryGetProperty("anchorDate", out var a) && a.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(a.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        switch (kind)
        {
            case Fixed:
                return off.All(d => d is >= 1 and <= 7) && off.Distinct().Count() == off.Count ? (new WeeklyOffPattern(kind, off, [], 7, anchor), null) : (null, DaysInvalid);
            case Alternating:
                if (anchor is null)
                {
                    return (null, AnchorRequired);
                }

                return off.Concat(alt).All(d => d is >= 1 and <= 7) && off.Distinct().Count() == off.Count && alt.Distinct().Count() == alt.Count
                    ? (new WeeklyOffPattern(kind, off, alt, 14, anchor), null) : (null, DaysInvalid);
            case Rotating:
                if (cycle is < 2 or > 31)
                {
                    return (null, CycleInvalid);
                }

                if (anchor is null)
                {
                    return (null, AnchorRequired);
                }

                return off.All(d => d >= 1 && d <= cycle) && off.Distinct().Count() == off.Count ? (new WeeklyOffPattern(kind, off, [], cycle, anchor), null) : (null, DaysInvalid);
            default:
                return (null, KindInvalid);
        }
    }

    /// <summary>True when the date is a weekly off under the pattern.</summary>
    public bool IsOff(DateOnly date)
    {
        switch (Kind)
        {
            case Fixed:
                return OffDays.Contains(Iso(date));
            case Alternating:
                var weeks = (MondayOf(date).DayNumber - MondayOf(Anchor!.Value).DayNumber) / 7;
                return (weeks % 2 == 0 ? OffDays : AltOffDays).Contains(Iso(date));
            default:
                var position = ((date.DayNumber - Anchor!.Value.DayNumber) % CycleDays + CycleDays) % CycleDays + 1;
                return OffDays.Contains(position);
        }
    }

    /// <summary>The weekly off dates from <paramref name="from"/> to <paramref name="to"/> inclusive.</summary>
    public IEnumerable<DateOnly> OffDates(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (IsOff(d))
            {
                yield return d;
            }
        }
    }

    private static int Iso(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    private static DateOnly MondayOf(DateOnly d) => d.AddDays(1 - Iso(d));

    private static List<int> Ints(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32())] : [];
}
