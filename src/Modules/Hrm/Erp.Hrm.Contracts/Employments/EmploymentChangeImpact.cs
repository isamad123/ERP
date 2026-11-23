namespace Erp.Hrm.Contracts.Employments;

/// <summary>
/// A change of an employment about to be saved, as the backdated change check describes it to the modules (slice S1.20 Build
/// item 8, ruling Q6 as corrected by CR-096). <see cref="EarliestDate"/> is the first day the change affects: the effective date,
/// or the earlier of the old and the new date when a correction moves a date. <see cref="IsTermination"/> marks an end of the
/// employment (S1.25 separations): only that is refused inside a locked period without an approved override (BR-02).
/// </summary>
/// <para>
/// <see cref="CarryOver"/> is set for a transfer between companies (slice S1.20 Build item 11, ruling Q9, FR-XM-30): the check then
/// runs whatever the date, and each module names, in <see cref="EmploymentImpactAnswer.OpenItems"/>, the open items of the employee
/// that the chosen carry-over does not carry (for example an open loan when loans are neither carried nor settled by the rule). The
/// transfer is refused while any are named (S1.20c).
/// </para>
public sealed record EmploymentChange(Guid EmploymentId, Guid CompanyId, string Kind, DateOnly EffectiveDate, DateOnly EarliestDate, bool IsTermination, TransferCarryOver? CarryOver = null);

/// <summary>
/// The three explicit carry-over choices of a transfer between companies (FR-XM-30, ruling Q9), as recorded on its events:
/// <see cref="Service"/> <c>keep</c> or <c>restart</c>; <see cref="Leave"/> <c>carry</c>, <c>encash</c> or <c>lapse</c> (applied by the
/// leave module, S1.32); <see cref="Loans"/> <c>carry</c> or <c>settle</c> (applied by the loans module, S1.41).
/// </summary>
public sealed record TransferCarryOver(string Service, string Leave, string Loans)
{
    public static IReadOnlyList<string> ServiceChoices { get; } = ["keep", "restart"];

    public static IReadOnlyList<string> LeaveChoices { get; } = ["carry", "encash", "lapse"];

    public static IReadOnlyList<string> LoanChoices { get; } = ["carry", "settle"];
}

/// <summary>A period a module has locked or calculated for the employment (attendance locked at payroll calculation, a payroll run).</summary>
/// <param name="Kind"><c>attendance</c> or <c>payroll</c> (or another module's own word).</param>
/// <param name="From">First day of the period.</param>
/// <param name="To">Last day of the period (inclusive).</param>
/// <param name="Locked">True when the period is locked (FR-XM-26): a change there becomes a retroactive adjustment in the next open period.</param>
public sealed record AffectedPeriod(string Kind, DateOnly From, DateOnly To, bool Locked);

/// <summary>
/// One module's answer (GHR-04): the periods from <see cref="EmploymentChange.EarliestDate"/> on whose results it
/// will recalculate (flagged, never refused; FR-XM-26), the employee's last attendance day (BR-02, for terminations) and notes as
/// localization keys. For a transfer (<see cref="EmploymentChange.CarryOver"/> set) <c>OpenItems</c> are localization keys of the open
/// items the chosen carry-over does not carry; the transfer is refused while any are named (S1.20c). Empty or null otherwise.
/// </summary>
public sealed record EmploymentImpactAnswer(string Module, IReadOnlyList<AffectedPeriod> Periods, DateOnly? LastAttendanceDate, IReadOnlyList<string> Notes, IReadOnlyList<string>? OpenItems = null);

/// <summary>
/// What a backdated employment change would disturb (GHR-04; slice S1.20 ruling Q6, CR-096). The HR module asks every registered
/// implementation before a change dated before today is saved and shows the answers; attendance (S1.29, S1.31), leave (S1.32)
/// and payroll (S1.35) register theirs. Answers are read-only: a module recalculates when it receives
/// <see cref="EmploymentEvents.EmployeeChanged"/>, whose <c>earliestDate</c> is the same as <see cref="EmploymentChange.EarliestDate"/>.
/// </summary>
public interface IEmploymentChangeImpact
{
    /// <summary>The answering module's code, shown with its answer.</summary>
    string ModuleCode { get; }

    Task<EmploymentImpactAnswer> AnalyseAsync(EmploymentChange change, CancellationToken cancellationToken);
}
