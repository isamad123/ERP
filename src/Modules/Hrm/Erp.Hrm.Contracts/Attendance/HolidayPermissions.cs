namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permissions of the holiday additions and the effective-shift answer (slice S1.28b, CR-085 S1.28b detail). Declared in
/// <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>; the choices themselves are read through the generated
/// <c>hrm.optional_holiday_choices.read</c>.
/// </summary>
public static class HolidayPermissions
{
    /// <summary>See the holidays of a calendar with their Hijri dates and the optional ones an employee may choose (FR-HRM-ATT-15).</summary>
    public const string HolidaysRead = "hrm.holidays.read";

    /// <summary>Choose or change optional holidays for an employment inside the caller's data scope (FR-HRM-ATT-15); self-service (S1.26) uses the same code.</summary>
    public const string OptionalHolidaysChoose = "hrm.optional_holidays.choose";

    /// <summary>See the shift an employee works on a day and why (FR-HRM-ATT-16).</summary>
    public const string EffectiveShiftRead = "hrm.shift_effective.read";

    public static IReadOnlyList<string> HandWritten { get; } = [HolidaysRead, OptionalHolidaysChoose, EffectiveShiftRead];
}

