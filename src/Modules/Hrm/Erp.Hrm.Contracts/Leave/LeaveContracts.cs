namespace Erp.Hrm.Contracts.Leave;

/// <summary>
/// One day of unpaid leave for payroll (contract of slice S1.32 Build item 10, FR-HRM-LVE-07): the approved request, its leave type, the share
/// of the day (1 a whole day, 0.5 a half; for a type counted in hours <paramref name="Days"/> is 0 and <paramref name="Hours"/> holds the hours)
/// and whether the day's attendance period is locked (FR-XM-26: a locked day no longer changes; a later change is a retro adjustment).
/// </summary>
public sealed record LeaveDeductionDay(Guid EmploymentId, DateOnly Date, Guid LeaveRequestId, Guid LeaveTypeId, string LeaveTypeCode, decimal Days, decimal? Hours, bool Locked);

/// <summary>
/// Leave without pay as a deduction input for the payroll engine (slice S1.32 Build item 10; S1.35 calculates the deduction). Answers the days of
/// approved requests of leave types that are unpaid and marked as a pay deduction (<c>is_paid = false</c> and <c>is_unpaid_deduction = true</c>),
/// for the employments and dates asked, ordered by employment and date. Days of a request waiting for its cancellation still count until the
/// cancellation is approved. No permission check: the caller (the payroll engine) has its own.
/// </summary>
public interface ILeaveDeductions
{
    Task<IReadOnlyList<LeaveDeductionDay>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}

/// <summary>
/// Leave paid out (contract of slice S1.32 Build items 12 and 14): one <c>encashment</c> entry of the ledger, its units (positive, in the type's
/// unit), the rate basis the policy names (a code payroll understands, for example basic or gross; null when the policy names none) and where it
/// came from (<c>year_end</c> with the run, or <c>transfer</c> with the employment event).
/// </summary>
public sealed record LeaveEncashment(Guid EntryId, Guid EmploymentId, Guid LeaveTypeId, string LeaveTypeCode, string Unit, int LeaveYear, DateOnly EntryDate, decimal Units, string? RateBasis, string Source, Guid? SourceId);

/// <summary>
/// The leave units to pay out for the payroll engine (slice S1.32 Build items 12 and 14; S1.35 and S1.43 calculate the amount): the encashment
/// entries dated in the range for the employments asked. No permission check: the caller has its own.
/// </summary>
public interface ILeaveEncashments
{
    Task<IReadOnlyList<LeaveEncashment>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}

/// <summary>Events of the leave engine (slice S1.32b, CR-086).</summary>
public static class LeaveEvents
{
    /// <summary>
    /// Compensatory off was earned (FR-HRM-LVE-08, ruling Q6; published by the attendance engine of S1.29b for approved overtime or work on a holiday
    /// or weekly off): payload <c>employmentId</c>, <c>companyId</c>, <c>workDate</c> (the day worked), <c>days</c> (the share of a day earned, for
    /// types taken in days or half days), <c>minutes</c> (the minutes earned, for types counted in hours), <c>sourceId</c> (the overtime entry or
    /// attendance day; the same source earns once) and optionally <c>leaveTypeCode</c> (the comp-off type when the company has several). The leave
    /// engine posts one <c>comp_off_earned</c> entry, expiring after the policy's <c>compOffExpiryDays</c>.
    /// </summary>
    public const string CompOffEarned = "hrm.attendance.comp_off_earned";
}
