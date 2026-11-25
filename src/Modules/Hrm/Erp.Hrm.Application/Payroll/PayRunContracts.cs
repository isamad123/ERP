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
/// Payroll runs (slice S1.35a Build items 2 to 7): create a regular run of a pay group and period, calculate it (lock the period's attendance and leave
/// first, then snapshot and calculate every employee in batches; employees that cannot be calculated are listed and the run does not finish), and
/// read the run and its results. Amounts are salary-class data (GHR-06).
/// </summary>
public interface IPayRunService
{
    Task<PeopleResult> CreateAsync(PayRunCreateRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> CalculateAsync(Guid runId, PayRunCalculateRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The run's results with their lines (one employment when given), inside the caller's data scope, amounts by the field class.</summary>
    Task<PeopleResult> ResultsAsync(Guid runId, Guid? employmentId, CancellationToken cancellationToken);
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
