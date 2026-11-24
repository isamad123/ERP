namespace Erp.Kernel.Contracts.Attendance;

/// <summary>
/// Whether the attendance and leave of an employment on a date are locked because payroll was calculated for the period (FR-XM-26;
/// slice S1.32 ruling Q9, CR-086). A locked date never changes again: a correction is dated in the next open period. The leave engine
/// (S1.32) asks before it posts a manual ledger entry; S1.31 adds locking and reopening and replaces the HR module's reading
/// implementation with the real one (CR-091). Kept in the kernel's contracts so any module may ask (D-10).
/// </summary>
public interface IAttendancePeriodLock
{
    /// <summary>True when the employment's attendance period covering <paramref name="day"/> is locked.</summary>
    Task<bool> IsLockedAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken);
}
