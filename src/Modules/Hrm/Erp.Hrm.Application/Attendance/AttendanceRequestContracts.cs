using System.Globalization;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// A request to correct a day's punches (slice S1.30a Build items 1 and 3; FR-HRM-ATT-11, GHR-09). The person is <see cref="EmploymentId"/>, or the
/// caller's own employment when absent; the day is <see cref="WorkDate"/>, or the day of <see cref="ExceptionId"/> when an exception is named.
/// <see cref="RequestedIn"/> and <see cref="RequestedOut"/> are the times the person says they came and left (one or both).
/// </summary>
public sealed class RegularizationRequestInput
{
    public Guid? EmploymentId { get; init; }

    public Guid? ExceptionId { get; init; }

    public DateOnly? WorkDate { get; init; }

    public DateTimeOffset? RequestedIn { get; init; }

    public DateTimeOffset? RequestedOut { get; init; }

    /// <summary>The time in as a clock time of the company's day (the web form's way); <see cref="RequestedInDayOffset"/> moves it to the day before (-1) or after (1).</summary>
    public TimeOnly? RequestedInTime { get; init; }

    public int RequestedInDayOffset { get; init; }

    /// <summary>The time out as a clock time; <see cref="RequestedOutDayOffset"/> 1 is the morning after a night shift.</summary>
    public TimeOnly? RequestedOutTime { get; init; }

    public int RequestedOutDayOffset { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// A request for on duty, official visit, work from home or field duty over days (slice S1.30a Build items 2 and 3; FR-HRM-ATT-14). Without times the
/// request covers whole days (the first day from its start to the end of the last day, in the company's time zone).
/// </summary>
public sealed class DutyRequestInput
{
    public Guid? EmploymentId { get; init; }

    public string? Kind { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public string? Purpose { get; init; }
}

/// <summary>
/// The attendance request services (slice S1.30a): make a request for oneself or, by data scope, for another person (the acting user is recorded
/// as <c>created_by</c>); the workflow decides it (document types <c>hrm.regularization</c> and <c>hrm.duty_request</c>); nobody approves their own
/// request (FR-XM-32). A date in a locked attendance period is refused (FR-XM-26; a retroactive adjustment is S1.31's).
/// </summary>
public interface IAttendanceRequestService
{
    Task<PeopleResult> RequestRegularizationAsync(RegularizationRequestInput request, CancellationToken cancellationToken);

    Task<PeopleResult> RequestDutyAsync(DutyRequestInput request, CancellationToken cancellationToken);
}

/// <summary>
/// The rules of an attendance request that need no database (slice S1.30a): which fields are needed, the order of the times and how far a time may
/// lie from its day. Every answer is a localization key per field. No limit of a duration is invented here (the specification names none).
/// </summary>
public static class AttendanceRequestRules
{
    public const string Required = "errors.data.required";
    public const string TooLong = "errors.hrm.attendance.request.reason_too_long";
    public const string Future = "errors.hrm.attendance.request.future";
    public const string TimesRequired = "errors.hrm.attendance.request.times_required";
    public const string TimesOrder = "errors.hrm.attendance.request.times_order";
    public const string TimeOffDay = "errors.hrm.attendance.request.time_off_day";
    public const string KindUnknown = "errors.hrm.attendance.request.kind_unknown";
    public const string PeriodOrder = "errors.hrm.attendance.request.period_order";

    public const int ReasonLength = 500;

    /// <summary>Field errors of a regularization request for <paramref name="workDate"/> (empty when it is acceptable).</summary>
    public static Dictionary<string, string[]> CheckRegularization(DateOnly workDate, DateOnly today, DateTimeOffset? requestedIn, DateTimeOffset? requestedOut, string? reason, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (workDate > today)
        {
            errors["workDate"] = [Future];
        }

        if (requestedIn is null && requestedOut is null)
        {
            errors["requestedIn"] = [TimesRequired];
        }
        else if (requestedIn is { } i && requestedOut is { } o && o <= i)
        {
            errors["requestedOut"] = [TimesOrder];
        }

        // A punch of a night shift lands on the next morning, and an early arrival on the evening before: one day either side is allowed.
        foreach (var (name, at) in new[] { ("requestedIn", requestedIn), ("requestedOut", requestedOut) })
        {
            if (at is { } value && !errors.ContainsKey(name))
            {
                var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
                if (local < workDate.AddDays(-1) || local > workDate.AddDays(1))
                {
                    errors[name] = [TimeOffDay];
                }
            }
        }

        AddReason(errors, "reason", reason);
        return errors;
    }

    /// <summary>Field errors of a duty request (empty when it is acceptable).</summary>
    public static Dictionary<string, string[]> CheckDuty(string? kind, DateOnly? start, DateOnly? end, TimeOnly? startTime, TimeOnly? endTime, string? purpose)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(kind))
        {
            errors["kind"] = [Required];
        }
        else if (!AttendancePolicy.DutyKinds.ContainsKey(kind))
        {
            errors["kind"] = [KindUnknown];
        }

        if (start is null)
        {
            errors["startDate"] = [Required];
        }

        if (end is null)
        {
            errors["endDate"] = [Required];
        }
        else if (start is { } s && end < s)
        {
            errors["endDate"] = [PeriodOrder];
        }

        if (start is { } a && end is { } b && a == b && startTime is { } t1 && endTime is { } t2 && t2 <= t1)
        {
            errors["endTime"] = [TimesOrder];
        }

        AddReason(errors, "purpose", purpose);
        return errors;
    }

    /// <summary>The instants a duty request covers: its times, or whole days from the start of the first to the end of the last (local time of <paramref name="zone"/>).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) DutyInstants(DateOnly start, DateOnly end, TimeOnly? startTime, TimeOnly? endTime, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var from = start.ToDateTime(startTime ?? TimeOnly.MinValue);
        var to = endTime is { } e ? end.ToDateTime(e) : end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (new DateTimeOffset(from, zone.GetUtcOffset(from)), new DateTimeOffset(to, zone.GetUtcOffset(to)));
    }

    /// <summary>The first and last local day a duty covers (an end exactly at midnight belongs to the day before).</summary>
    public static (DateOnly First, DateOnly Last) DutyDays(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var last = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        var lastDay = DateOnly.FromDateTime(last.TimeOfDay == TimeSpan.Zero ? last.AddTicks(-1) : last);
        return (first, lastDay < first ? first : lastDay);
    }

    /// <summary>The instant of a clock time on <paramref name="day"/> plus <paramref name="dayOffset"/> days, in the company's zone.</summary>
    public static DateTimeOffset LocalInstant(DateOnly day, TimeOnly time, int dayOffset, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = day.AddDays(Math.Clamp(dayOffset, -1, 1)).ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The time as the approval card shows it, in the company's local time ("HH:mm").</summary>
    public static string LocalTime(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static void AddReason(Dictionary<string, string[]> errors, string name, string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            errors[name] = [Required];
        }
        else if (value.Length > ReasonLength)
        {
            errors[name] = [TooLong];
        }
    }
}
