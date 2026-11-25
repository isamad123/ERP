namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permissions of the attendance period lock and its adjustments (slice S1.31, CR-091 S1.31 detail). The generated writes of
/// <c>hrm.attendance_periods</c> and <c>hrm.attendance_adjustments</c> are closed; reads stay the generated <c>&lt;table&gt;.read</c> by data scope.
/// Every lock, reopen and adjustment is decided through the workflow, never by the person who asked (FR-XM-32).
/// </summary>
public static class AttendanceLockPermissions
{
    public const string PeriodsRead = "hrm.attendance_periods.read";

    public const string AdjustmentsRead = "hrm.attendance_adjustments.read";

    /// <summary>Ask for HR's lock of a pay period after its cut-off date (FR-HRM-ATT-17, ruling Q1).</summary>
    public const string Lock = "hrm.attendance_periods.lock";

    /// <summary>Ask for the run that locked a period to be cancelled and the period reopened (FR-HRM-ATT-21, CR-095).</summary>
    public const string Reopen = "hrm.attendance_periods.reopen";

    /// <summary>Ask for a retroactive adjustment of a locked date, and see its impact first (FR-XM-26, GHR-04), by data scope.</summary>
    public const string AdjustmentsSubmit = "hrm.attendance_adjustments.submit";

    /// <summary>Every code declared in permissions.json for these services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [Lock, Reopen, AdjustmentsSubmit];
}

/// <summary>Events and workflow document types of the attendance period lock (slice S1.31, ruling Q6; <c>hrm.outbox_messages</c>, <c>workflow.json</c>).</summary>
public static class AttendanceLockEvents
{
    public const string AggregateType = "hrm.attendance_period";

    /// <summary>
    /// A pay period's attendance and leave locked (FR-HRM-ATT-21; used by payroll): payload <c>periodId</c>, <c>payPeriodId</c>, <c>payGroupId</c>,
    /// <c>companyId</c>, <c>periodStart</c>, <c>periodEnd</c>, <c>lockKind</c> (<c>payroll</c> or <c>manual</c>), <c>runId</c>, <c>lockedBy</c>,
    /// <c>unprocessedAccepted</c>, <c>exceptionsAccepted</c> and <c>employmentIds</c> (every employment of the pay group in the period).
    /// </summary>
    public const string Locked = "hrm.attendance.locked";

    /// <summary>
    /// A pay period reopened because the run that locked it was cancelled (FR-HRM-ATT-21, CR-095): payload as <see cref="Locked"/> without the lock
    /// counts, with <c>reason</c>, <c>requestedBy</c>, <c>approvedBy</c> and <c>employmentIds</c>, the affected employments.
    /// </summary>
    public const string Reopened = "hrm.attendance.reopened";

    /// <summary>
    /// A retroactive adjustment approved (FR-XM-26): payload <c>adjustmentId</c>, <c>employmentId</c>, <c>companyId</c>, <c>kind</c>, <c>fromDate</c>,
    /// <c>toDate</c>, <c>periodId</c>, <c>originalPayPeriodId</c>, <c>targetPayPeriodId</c>. Payroll reads the detail through <see cref="IAttendanceAdjustments"/>.
    /// </summary>
    public const string AdjustmentApproved = "hrm.attendance.adjustment_approved";

    /// <summary>HR's lock of a pay period before payroll; the document is the <c>hrm.attendance_periods</c> row.</summary>
    public const string LockDocumentType = "hrm.attendance_period_lock";

    /// <summary>Cancelling the run that locked a period and reopening it; the document is the <c>hrm.attendance_periods</c> row.</summary>
    public const string ReopenDocumentType = "hrm.attendance_period_reopen";

    /// <summary>A retroactive adjustment; the document is the <c>hrm.attendance_adjustments</c> row.</summary>
    public const string AdjustmentDocumentType = "hrm.attendance_adjustment";
}

/// <summary>
/// One day as payroll read it, or as corrected (slice S1.31 Build item 5): the result (null when the correction does not state one), the minutes
/// worked, late and early, the payable overtime minutes, and the unpaid and paid leave of the day in days (hours for leave counted in hours).
/// </summary>
public sealed record AdjustedDaySummary(string? Result, int WorkedMinutes, int LateMinutes, int EarlyMinutes, int OvertimeMinutes, decimal UnpaidLeave, decimal PaidLeave);

/// <summary>A day of an adjustment: its date, what was locked and what it should have been.</summary>
public sealed record AdjustedDay(DateOnly Date, AdjustedDaySummary Old, AdjustedDaySummary New);

/// <summary>
/// An approved retroactive adjustment for payroll (FR-XM-26): the employment, the kind (<c>attendance</c>, <c>leave_cancellation</c>, <c>overtime</c>), the
/// dates of the locked period and its pay period, the pay period it is carried into, the days, the leave request cancelled and the status
/// (<c>approved</c>, or <c>applied</c> once a run used it, S1.39).
/// </summary>
public sealed record AttendanceAdjustmentInput(
    Guid AdjustmentId, Guid EmploymentId, string Kind, DateOnly FromDate, DateOnly ToDate, Guid OriginalPayPeriodId, Guid TargetPayPeriodId,
    IReadOnlyList<AdjustedDay> Days, Guid? LeaveRequestId, string Status);

/// <summary>
/// The approved retroactive adjustments payroll carries into a pay period (slice S1.31 Build item 5; S1.35 and S1.39 read it): approved or applied
/// adjustments of the employments asked whose target is the pay period, ordered by employment and date. The locked days themselves never change.
/// No permission check: the caller (the payroll engine) has its own.
/// </summary>
public interface IAttendanceAdjustments
{
    Task<IReadOnlyList<AttendanceAdjustmentInput>> ForPayPeriodAsync(IReadOnlyCollection<Guid> employmentIds, Guid payPeriodId, CancellationToken cancellationToken);
}
