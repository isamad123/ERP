namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permissions of the lateness and overtime services (slice S1.29b, CR-089 S1.29b detail). The generated writes of <c>hrm.overtime_entries</c> and
/// <c>hrm.attendance_penalties</c> are closed; reads stay the generated <c>&lt;table&gt;.read</c> by data scope. Deciding goes through the workflow.
/// </summary>
public static class AttendanceConsequencePermissions
{
    public const string OvertimeRead = "hrm.overtime_entries.read";

    public const string PenaltiesRead = "hrm.attendance_penalties.read";

    /// <summary>Claim worked overtime, or ask for overtime before the day, for oneself or, by data scope, for another person (FR-HRM-ATT-12).</summary>
    public const string OvertimeSubmit = "hrm.overtime_entries.submit";

    /// <summary>Ask for a lateness penalty to be waived, with a reason (FR-HRM-ATT-13); an approver other than the employee decides (FR-XM-32).</summary>
    public const string PenaltyWaive = "hrm.attendance_penalties.waive";

    /// <summary>Every code declared in permissions.json for these services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [OvertimeSubmit, PenaltyWaive];
}

/// <summary>Workflow document types of slice S1.29b (<c>workflow.json</c>).</summary>
public static class AttendanceConsequenceEvents
{
    /// <summary>Overtime asked for before the day or claimed after it; the document is the <c>hrm.overtime_entries</c> row.</summary>
    public const string OvertimeDocumentType = "hrm.overtime";

    /// <summary>A waiver of a lateness penalty; the document is the <c>hrm.attendance_penalties</c> row.</summary>
    public const string WaiverDocumentType = "hrm.attendance_penalty_waiver";
}

/// <summary>
/// Approved overtime of one day for payroll (contract of slice S1.29 Build item 11, ruling Q8; S1.35 calculates the pay): the entry, the employment,
/// the day and its attendance period (null while the day is not processed), the day type, the payable minutes (the approved minutes, never more than
/// those worked), the rate multiplier of the policy, the entry's status (<c>approved</c>, or <c>paid</c> once payroll has used it) and whether the
/// day's attendance period is locked (FR-XM-26: a locked day no longer changes; a later change is a retroactive adjustment, S1.31).
/// </summary>
public sealed record OvertimeInput(Guid EntryId, Guid EmploymentId, DateOnly WorkDate, Guid? PeriodId, string DayType, int Minutes, decimal Multiplier, string Status, bool Locked);

/// <summary>
/// Approved overtime as a payroll input (slice S1.29 Build item 11): the approved or paid entries dated in the range for the employments asked with
/// payable minutes above zero, ordered by employment and date. Overtime converted to compensatory off is not paid and is not listed. No permission
/// check: the caller (the payroll engine) has its own.
/// </summary>
public interface IOvertimeInputs
{
    Task<IReadOnlyList<OvertimeInput>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}

/// <summary>
/// A lateness pay deduction for payroll (contract of slice S1.29 Build item 7, ruling Q8; S1.35 works out the amount): the penalty, the employment,
/// the day and its attendance period, <c>late</c> or <c>early</c>, the minutes, which late (or early) day of the calendar month it was, and whether
/// the day's period is locked.
/// </summary>
public sealed record LatenessDeduction(Guid PenaltyId, Guid EmploymentId, DateOnly WorkDate, Guid PeriodId, string Kind, int Minutes, int Occurrence, bool Locked);

/// <summary>
/// The lateness penalties whose action is a pay deduction (slice S1.29 Build item 7): applied penalties dated in the range for the employments asked,
/// ordered by employment, date and kind. A waived or cancelled penalty is not listed (its record stays). No permission check: the caller has its own.
/// </summary>
public interface ILatenessDeductions
{
    Task<IReadOnlyList<LatenessDeduction>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}
