namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permissions of the attendance engine (slice S1.29a, CR-089 S1.29a detail). The generated writes of <c>hrm.attendance_days</c>,
/// <c>hrm.attendance_exceptions</c> and <c>hrm.overtime_entries</c> are closed: only the engine writes them. Reads stay the generated
/// <c>&lt;table&gt;.read</c> (the employee's own days, the manager's team, HR's company, by data scope).
/// </summary>
public static class AttendanceEnginePermissions
{
    public const string DaysRead = "hrm.attendance_days.read";

    public const string ExceptionsRead = "hrm.attendance_exceptions.read";

    /// <summary>Process an employment's days now, or queue the job <c>hrm.attendance_process</c> for a company or unit (FR-HRM-ATT-10).</summary>
    public const string Process = "hrm.attendance_days.process";

    /// <summary>The unmapped device punches shown beside the exception queue (S1.27b's read permission).</summary>
    public const string UnmappedRead = "hrm.unmapped_punches.read";

    /// <summary>Every code declared in permissions.json for the engine (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [Process];
}

/// <summary>Events of the attendance engine in <c>hrm.outbox_messages</c> (slice S1.29a, CR-089).</summary>
public static class AttendanceEngineEvents
{
    public const string AggregateType = "hrm.attendance_day";

    /// <summary>
    /// An exception was raised for a day (FR-HRM-ATT-11): payload <c>exceptionId</c>, <c>employmentId</c>, <c>companyId</c>, <c>workDate</c>,
    /// <c>kind</c>. Raised once per exception row; a reprocessed day that still has the exception raises nothing new.
    /// </summary>
    public const string ExceptionRaised = "hrm.attendance.exception_raised";

    /// <summary>
    /// Days of an employment must be worked out again (slice S1.29 Build item 6): payload <c>employmentId</c>, <c>from</c>, <c>to</c>,
    /// <c>reason</c>. Written by <see cref="IAttendanceReprocessing"/> in the transaction of the change; handled by the engine's consumer.
    /// </summary>
    public const string DaysRequeued = "hrm.attendance.days_requeued";
}

/// <summary>
/// An approved leave day as the attendance engine needs it (slice S1.29 Build item 2): the request, the share of the day it takes (1 is the
/// whole day) and which half (<c>full</c>, <c>first_half</c>, <c>second_half</c>); for a leave type counted in hours, <paramref name="Minutes"/>
/// holds the leave's minutes on the day and <paramref name="Quantity"/> is 0 (review S1.29a finding 4).
/// </summary>
public sealed record ApprovedLeaveDay(DateOnly Date, Guid LeaveRequestId, decimal Quantity, string Part, int? Minutes = null);

/// <summary>
/// The approved leave of an employment by day (contract of S1.29a; the leave requests are S1.32b's). The HR module answers it from
/// <c>hrm.leave_request_days</c> of approved requests; S1.32b keeps those rows and calls <see cref="IAttendanceReprocessing"/> when a request is
/// approved, cancelled or changed.
/// </summary>
public interface IApprovedLeaveDays
{
    Task<IReadOnlyList<ApprovedLeaveDay>> ForAsync(Guid employmentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}

/// <summary>
/// An approved duty request touching a day (<c>on_duty</c>, <c>official_visit</c>, <c>work_from_home</c>; S1.30 adds <c>field_duty</c>) with its time:
/// it decides the day when it covers the whole shift, otherwise its time counts as time at work (CR-089 S1.29a detail m).
/// </summary>
public sealed record ApprovedDutyDay(DateOnly Date, Guid DutyRequestId, string Kind, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null);

/// <summary>
/// The approved duty of an employment by day (contract of S1.29a; the duty requests are S1.30's). The HR module answers it from approved
/// <c>hrm.duty_requests</c> by the company's local dates they cover; S1.30 adds "counts as present" per kind and calls
/// <see cref="IAttendanceReprocessing"/> on approval.
/// </summary>
public interface IApprovedDutyDays
{
    Task<IReadOnlyList<ApprovedDutyDay>> ForAsync(Guid employmentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}

/// <summary>
/// Asks the engine to work out days again (slice S1.29 Build item 6): a late punch or an approved request re-queues the days it touches,
/// and only those. The request is an outbox event written with the caller's own change of the HR database (saved by the caller's
/// <c>SaveChanges</c>), so it is never lost and never runs for a change that was rolled back. A day in a locked period is not reprocessed (FR-XM-26).
/// </summary>
public interface IAttendanceReprocessing
{
    void Requeue(Guid employmentId, DateOnly firstDay, DateOnly lastDay, string reason);
}
