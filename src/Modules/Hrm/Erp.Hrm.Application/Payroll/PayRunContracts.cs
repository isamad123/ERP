using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Payroll;

/// <summary>Body of <c>POST /api/hrm/pay-runs</c>: the pay group and the period of a regular run.</summary>
public sealed record PayRunCreateRequest(Guid? PayGroupId, Guid? PayPeriodId);

/// <summary>
/// Body of <c>POST /api/hrm/pay-runs/{id}/calculate</c>: whether the attendance lock may accept days not processed and open exceptions (FR-XM-26; the
/// counts are recorded on the attendance period).
/// </summary>
public sealed record PayRunCalculateRequest(bool AcceptOpenItems = false);

/// <summary>
/// Body of <c>POST /api/hrm/pay-runs/{id}/recalculate</c> (slice S1.35b Build item 11): the employments to calculate again (all the run's employees
/// when empty), whether from a fresh snapshot (else each employee's stored one), and the lock's flag as for a calculation.
/// </summary>
public sealed record PayRunRecalculateRequest(IReadOnlyList<Guid>? EmploymentIds = null, bool FreshSnapshot = false, bool AcceptOpenItems = false);

/// <summary>
/// Payroll runs (slice S1.35a Build items 2 to 7, S1.35b items 8 to 11): create a regular run of a pay group and period, calculate it (lock the period's
/// attendance and leave first, then the job <c>hrm.payroll_calculate</c> snapshots and calculates every employee in chunks, in lanes; employees that
/// cannot be calculated are listed and the run does not finish), recalculate some or all of its employees, cancel a calculation, and read the run with
/// its progress, its results and a result's trace. Amounts are salary-class data (GHR-06).
/// </summary>
public interface IPayRunService
{
    Task<PeopleResult> CreateAsync(PayRunCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Locks the period first (FR-XM-26), then queues the calculation's lanes; answers 202 with the request and its job runs.</summary>
    Task<PeopleResult> CalculateAsync(Guid runId, PayRunCalculateRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The recalculation hook (Build item 11; the stage rules of when it may be used are S1.38's): the listed employees (all when none) are calculated
    /// again from their stored snapshots or fresh ones; their earlier results are replaced as new row versions (the audit log keeps every version) and
    /// the run is calculated again when every employee has a result. Refused once the run is locked (PAY-33).
    /// </summary>
    Task<PeopleResult> RecalculateAsync(Guid runId, PayRunRecalculateRequest request, CancellationToken cancellationToken);

    /// <summary>Asks the running calculation of the run to stop at its next chunk (results saved so far stay; a later calculation goes on).</summary>
    Task<PeopleResult> CancelCalculationAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The run with its counts and the progress of its last calculation (lanes, job states, employees done).</summary>
    Task<PeopleResult> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The run's results with their lines (one employment when given), inside the caller's data scope, amounts by the field class.</summary>
    Task<PeopleResult> ResultsAsync(Guid runId, Guid? employmentId, CancellationToken cancellationToken);

    /// <summary>The trace of one result (FR-HRM-PAY-32): needs <c>hrm.pay.trace.read</c>, the data scope of the result and the salary field class.</summary>
    Task<PeopleResult> TraceAsync(Guid resultId, CancellationToken cancellationToken);
}

/// <summary>One one-time input: employment, period, component, an amount or units (paid at the component's rate), the source type and its id.</summary>
public sealed record PayInputLine(Guid? EmploymentId, Guid? PayPeriodId, Guid? ComponentId, decimal? Amount, decimal? Units, string? SourceType, Guid? SourceId);

/// <summary>Body of <c>POST /api/hrm/pay-inputs</c>: the inputs to add together (all or none).</summary>
public sealed record PayInputsRequest(IReadOnlyList<PayInputLine>? Items);

/// <summary>
/// One-time payroll inputs (slice S1.35a Build item 1, ruling Q5; FR-HRM-PAY-07, -08): the validated bulk insert of <c>hrm.pay_inputs</c> rows (bonuses,
/// incentives, deductions, adjustments; the import centre writes through the same checks) and the cancellation of an input no run has used.
/// </summary>
public interface IPayInputService
{
    Task<PeopleResult> AddAsync(PayInputsRequest request, Guid? importBatchId, CancellationToken cancellationToken);

    Task<PeopleResult> CancelAsync(Guid inputId, CancellationToken cancellationToken);

    /// <summary>The named inputs a formula may use (the catalogue for formula authors; needs the read right on pay components).</summary>
    Task<PeopleResult> InputNamesAsync(CancellationToken cancellationToken);
}
