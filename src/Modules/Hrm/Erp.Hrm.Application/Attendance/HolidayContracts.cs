using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Attendance;

/// <summary>A holiday as the HR answers show it: the date, the names, the kind and the Hijri date (null when the calendar does not cover the day).</summary>
public sealed record HolidayView(
    Guid Id, Guid CalendarId, DateOnly Date, IReadOnlyDictionary<string, string> Name, string Kind, bool IsTentative, bool IsOptional, HijriDate? Hijri);

/// <summary>Choose the optional holidays of an employment for a year: the whole set replaces the earlier choices of that year.</summary>
public sealed class OptionalHolidayRequest
{
    public int? Year { get; init; }

    public IReadOnlyList<Guid>? HolidayIds { get; init; }
}

/// <summary>Holidays with Hijri dates and the optional-holiday choices (slice S1.28b Build items 6 and 7, FR-HRM-ATT-15).</summary>
public interface IHolidayService
{
    /// <summary>The holidays of a company's calendar (its location's calendar when a location is named, or a named calendar) for a year, with Hijri dates.</summary>
    Task<PeopleResult> ListAsync(Guid companyId, Guid? locationId, Guid? calendarId, int year, CancellationToken cancellationToken);

    /// <summary>The holidays of an employment's calendar for a year with the employee's choices, the limit and the cut-off.</summary>
    Task<PeopleResult> ForEmploymentAsync(Guid employmentId, int year, CancellationToken cancellationToken);

    /// <summary>Replaces the choices of a year; refused above the yearly limit or after a holiday's cut-off.</summary>
    Task<PeopleResult> ChooseAsync(Guid employmentId, OptionalHolidayRequest request, CancellationToken cancellationToken);
}

/// <summary>Why a day is what it is: a localization key and its values.</summary>
public sealed record EffectiveReason(string Key, IReadOnlyDictionary<string, string>? Values = null);

/// <summary>The season that changed the times of a day.</summary>
public sealed record AppliedSeason(Guid PolicyId, string PolicyCode, string SeasonName, TimeOnly? Start, TimeOnly? End, int? BreakMinutes, int? ShortenPercent);

/// <summary>
/// What an employment works on one day (slice S1.28b Build item 8, FR-HRM-ATT-16). <see cref="DayType"/> is <c>working</c>, <c>weekly_off</c>,
/// <c>holiday</c> or <c>none</c> (nothing says what the day is). <see cref="Source"/> is <c>roster</c> (a published roster names the day),
/// <c>weekly_off_policy</c> (no roster day, the shift policy's weekly off) or <c>none</c>. The times are those after a season was applied; the
/// shift's own times stay in <see cref="BaseStart"/> and <see cref="BaseEnd"/>. <see cref="Reasons"/> says why.
/// </summary>
public sealed record EffectiveShift(
    Guid EmploymentId,
    DateOnly Date,
    string DayType,
    string Source,
    Guid? RosterId,
    Guid? ShiftId,
    string? ShiftCode,
    IReadOnlyDictionary<string, string>? ShiftName,
    string? ShiftType,
    TimeOnly? Start,
    TimeOnly? End,
    int? BreakMinutes,
    bool CrossesMidnight,
    TimeOnly? BaseStart,
    TimeOnly? BaseEnd,
    AppliedSeason? Season,
    HolidayView? Holiday,
    IReadOnlyList<EffectiveReason> Reasons);

/// <summary>
/// The shift an employment works on a day, with the reason, for other HR engines (the attendance day processing, S1.29): no permission check,
/// the caller has its own. Null when the employment does not exist.
/// </summary>
public interface IEffectiveShiftReader
{
    Task<EffectiveShift?> ReadAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>The effective-shift answer for the screens and the API, with the caller's permission and data scope.</summary>
public interface IEffectiveShiftService
{
    Task<PeopleResult> EffectiveAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken);
}
