
namespace Erp.Hrm.Application.Attendance;

/// <summary>A Hijri (Islamic) calendar date.</summary>
public readonly record struct HijriDate(int Year, int Month, int Day);

/// <summary>
/// The Hijri date shown beside a holiday (slice S1.28b Build item 6, FR-HRM-ATT-15, ruling Q4): the .NET Umm al-Qura calendar, moved by the
/// company's correction of -1, 0 or +1 day for the moon sighting. For display only: a Hijri date never decides pay or attendance.
/// </summary>
public static class HijriDates
{
    /// <summary>The Hijri month Ramzan (Ramadan) is.</summary>
    public const int Ramadan = 9;

    private static readonly System.Globalization.UmAlQuraCalendar Calendar = new();

    private static readonly DateOnly First = DateOnly.FromDateTime(Calendar.MinSupportedDateTime);

    private static readonly DateOnly Last = DateOnly.FromDateTime(Calendar.MaxSupportedDateTime);

    /// <summary>The Hijri date of a Gregorian day moved by <paramref name="offsetDays"/>; null when the calendar does not cover it (1900 to 2077).</summary>
    public static HijriDate? Of(DateOnly date, int offsetDays = 0)
    {
        var moved = date.AddDays(offsetDays);
        if (moved < First || moved > Last)
        {
            return null;
        }

        var at = moved.ToDateTime(TimeOnly.MinValue);
        return new HijriDate(Calendar.GetYear(at), Calendar.GetMonth(at), Calendar.GetDayOfMonth(at));
    }

    /// <summary>True when the offset is one the setting allows.</summary>
    public static bool ValidOffset(int offsetDays) => offsetDays is >= -1 and <= 1;
}
