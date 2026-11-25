using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Attendance;

/// <summary>A claim of worked overtime (post-approval, slice S1.29b Build item 9) with the reason; the entry is the engine's worked-out one.</summary>
public sealed class OvertimeClaimInput
{
    public string? Reason { get; init; }
}

/// <summary>
/// Overtime asked for before the day (pre-approval, slice S1.29b Build item 9): the person (<see cref="EmploymentId"/>, or the caller's own employment),
/// the day (today or later in the company's time), the minutes asked for and the reason.
/// </summary>
public sealed class OvertimeRequestInput
{
    public Guid? EmploymentId { get; init; }

    public DateOnly? WorkDate { get; init; }

    public int? Minutes { get; init; }

    public string? Reason { get; init; }
}

/// <summary>A request to waive a lateness penalty (slice S1.29b Build item 8) with the reason the approver reads.</summary>
public sealed class PenaltyWaiverInput
{
    public string? Reason { get; init; }
}

/// <summary>
/// Overtime and lateness requests (slice S1.29b; FR-HRM-ATT-12, ATT-13, FR-XM-26, FR-XM-32): claim worked overtime, ask for overtime before the day, and
/// ask for a penalty to be waived, for oneself or, by data scope, for another person. The workflow decides (document types <c>hrm.overtime</c> and
/// <c>hrm.attendance_penalty_waiver</c>); the person concerned and the requester never approve; a date in a locked period is refused.
/// </summary>
public interface IAttendanceConsequenceService
{
    Task<PeopleResult> ClaimOvertimeAsync(Guid entryId, OvertimeClaimInput request, CancellationToken cancellationToken);

    Task<PeopleResult> RequestOvertimeAsync(OvertimeRequestInput request, CancellationToken cancellationToken);

    Task<PeopleResult> RequestWaiverAsync(Guid penaltyId, PenaltyWaiverInput request, CancellationToken cancellationToken);
}

/// <summary>The checks of an overtime request and a reason (pure; error keys as field errors).</summary>
public static class AttendanceConsequenceRules
{
    public const string Required = "errors.hrm.attendance.overtime.required";
    public const string NotBeforeToday = "errors.hrm.attendance.overtime.not_before_today";
    public const string MinutesOutOfRange = "errors.hrm.attendance.overtime.minutes_out_of_range";
    public const string ReasonTooLong = "errors.hrm.attendance.overtime.reason_too_long";

    /// <summary>The most minutes one request asks for (a day; any cap is the customer's policy).</summary>
    public const int MostMinutes = 1440;

    public const int ReasonLength = 1000;

    public static Dictionary<string, string[]> CheckRequest(DateOnly? workDate, int? minutes, string? reason, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (workDate is not { } date)
        {
            errors["workDate"] = [Required];
        }
        else if (date < today)
        {
            errors["workDate"] = [NotBeforeToday];
        }

        if (minutes is not { } m)
        {
            errors["minutes"] = [Required];
        }
        else if (m is < 1 or > MostMinutes)
        {
            errors["minutes"] = [MinutesOutOfRange];
        }

        CheckReason(reason, errors);
        return errors;
    }

    public static Dictionary<string, string[]> CheckReason(string? reason, Dictionary<string, string[]>? errors = null)
    {
        errors ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = [Required];
        }
        else if (reason.Trim().Length > ReasonLength)
        {
            errors["reason"] = [ReasonTooLong];
        }

        return errors;
    }
}
