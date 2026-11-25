using System.Data.Common;

namespace Erp.Kernel.Contracts.Attendance;

/// <summary>
/// The lock of attendance and leave of a pay period (FR-XM-26, FR-HRM-ATT-17, ATT-21; slices S1.32 ruling Q9 and S1.31, CR-091). A locked date never
/// changes again: a correction is a retroactive adjustment dated in the next open period. Any module may ask whether a date is locked; payroll's
/// calculation locks the pay period of its run (S1.35) and the cancellation of that run before it is locked itself reopens it (S1.38, CR-095);
/// HR may lock after the cut-off date with approval (S1.31 ruling Q1). The HR module answers it (S1.31's lock service). Kept in the kernel's
/// contracts so any module may ask (D-10).
/// </summary>
public interface IAttendancePeriodLock
{
    /// <summary>True when the employment's attendance period covering <paramref name="day"/> is locked.</summary>
    Task<bool> IsLockedAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the attendance and leave of a pay period of a pay group (Lock(period, run)): refused while employee days are not processed or exceptions
    /// are open unless <see cref="PeriodLockRequest.AcceptOpenItems"/> is set (the counts are recorded); a period already locked answers
    /// <see cref="PeriodLockOutcome.AlreadyLocked"/>. Publishes <c>hrm.attendance.locked</c> with the employments of the period. Runs in
    /// <paramref name="transaction"/> when given (the caller's), else in its own.
    /// </summary>
    Task<PeriodLockResult> LockAsync(PeriodLockRequest request, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Reopens a pay period (Reopen(period, reason, approval)) as part of cancelling the run that locked it, once that run is cancelled and only if it
    /// never reached locked (CR-095): an HR lock never reopens, nor a period a finalized run uses; the approval is never the requester's own
    /// (FR-XM-32). Publishes <c>hrm.attendance.reopened</c> with the affected employments. Any other reopen is refused ("use a retroactive adjustment").
    /// </summary>
    Task<PeriodLockResult> ReopenAsync(Guid payPeriodId, string reason, PeriodReopenApproval approval, DbTransaction? transaction, CancellationToken cancellationToken);
}

/// <summary>
/// What to lock: the pay period, the payroll run that locks it (null for HR's lock), whether days not processed and open exceptions are accepted (recorded
/// on the period), who locks, and for HR's lock its reason and approval request.
/// </summary>
public sealed record PeriodLockRequest(Guid PayPeriodId, Guid? RunId, bool AcceptOpenItems, Guid LockedBy, string? Reason = null, Guid? WorkflowInstanceId = null);

/// <summary>The approval of a reopen: who asked, who approved (never the same person, FR-XM-32) and the approval request.</summary>
public sealed record PeriodReopenApproval(Guid RequestedBy, Guid ApprovedBy, Guid? WorkflowInstanceId);

/// <summary>How a lock or reopen ended.</summary>
public enum PeriodLockOutcome
{
    Locked,
    AlreadyLocked,
    Reopened,
    Refused,
}

/// <summary>
/// The answer of a lock or reopen: the outcome, the attendance period, the reason of a refusal as a localization key, and the days not processed and the
/// open exceptions counted (the lock's own counts, or what refused it).
/// </summary>
public sealed record PeriodLockResult(PeriodLockOutcome Outcome, Guid? PeriodId, string? ErrorKey = null, int UnprocessedDays = 0, int OpenExceptions = 0);
