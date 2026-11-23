using System.Text.Json;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Employments;

/// <summary>
/// The three explicit carry-over choices of a transfer between companies (FR-XM-30, slice S1.20 ruling Q9). A choice left out takes
/// the receiving company's setting (<c>hrm.transfer.carry_over.service</c>, <c>.leave</c>, <c>.loans</c>); the events always record all three.
/// </summary>
public sealed class CarryOverInput
{
    /// <summary><c>keep</c> (the service start date carries over) or <c>restart</c> (service counts from the transfer date).</summary>
    public string? Service { get; init; }

    /// <summary><c>carry</c>, <c>encash</c> or <c>lapse</c> (applied by the leave module, S1.32).</summary>
    public string? Leave { get; init; }

    /// <summary><c>carry</c> or <c>settle</c> (applied by the loans module, S1.41).</summary>
    public string? Loans { get; init; }
}

/// <summary>
/// A transfer to another company of the customer (slice S1.20 Build item 11, FR-XM-30): the employment ends on the day before
/// <see cref="EffectiveDate"/> and a linked one starts on it in <see cref="ToCompanyId"/>, with a new employee number of that company
/// and the job details of <see cref="Assignment"/> (every required field, as for a hire).
/// </summary>
public sealed class TransferRequest
{
    public Guid? ToCompanyId { get; init; }

    /// <summary>The first day in the receiving company.</summary>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>The employee type in the receiving company (default: as now).</summary>
    public string? EmployeeType { get; init; }

    /// <summary>A typed employee number of the receiving company; only with <c>hrm.employments.number_override</c>.</summary>
    public string? EmployeeNo { get; init; }

    public string? Notes { get; init; }

    public AssignmentInput? Assignment { get; init; }

    public CarryOverInput? CarryOver { get; init; }

    /// <summary>The employment's row version the caller read (optimistic concurrency).</summary>
    public long? RowVersion { get; init; }
}

/// <summary>
/// A rehire (slice S1.20 Build item 12, BR-01): a new employment of the person of a former employment, linked to it, with a
/// <c>rehire</c> event; the former employment is left as it is. The company defaults to the former one.
/// </summary>
public sealed class RehireRequest
{
    public Guid? CompanyId { get; init; }

    public string? EmployeeType { get; init; }

    public DateOnly? JoiningDate { get; init; }

    /// <summary>Defaults to the joining date; an earlier date is the customer's explicit choice to count earlier service.</summary>
    public DateOnly? ServiceStartDate { get; init; }

    public DateOnly? ExpectedEndDate { get; init; }

    public string? EmployeeNo { get; init; }

    /// <summary>A reason code of the rehire list (default <c>rejoining</c>).</summary>
    public string? ReasonCode { get; init; }

    public string? Notes { get; init; }

    public AssignmentInput? Assignment { get; init; }
}

/// <summary>Transfers between companies and rehires (slice S1.20c Build items 11 and 12). Answers as for the other employment services.</summary>
public interface IEmploymentMoves
{
    /// <summary>The validation, the carry-over as it would be recorded and the impact check of a transfer, without saving it.</summary>
    Task<PeopleResult> CheckTransferAsync(Guid employmentId, TransferRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> TransferAsync(Guid employmentId, TransferRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> RehireAsync(Guid formerEmploymentId, RehireRequest request, CancellationToken cancellationToken);
}

/// <summary>A contract of an employment (slice S1.20 Build item 13, FR-HRM-EMP-09). Terms are a JSON object (text values), at most 16 KB.</summary>
public sealed class ContractRequest
{
    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    /// <summary>Days before the end the renewal reminder goes out (default: the setting <c>hrm.contracts.renewal_notice_days</c>).</summary>
    public int? RenewalNoticeDays { get; init; }

    public JsonElement? Terms { get; init; }

    /// <summary>A stored file of the signed contract (doc.files).</summary>
    public Guid? FileId { get; init; }

    /// <summary>On a renewal: the renewed contract's row version (optimistic concurrency).</summary>
    public long? RowVersion { get; init; }
}

/// <summary>A probation decision (FR-HRM-EMP-10): extend to a new last day, confirm (a confirmation event) or not confirm.</summary>
public sealed class ProbationDecisionRequest
{
    /// <summary>Extend: the new last day of the probation.</summary>
    public DateOnly? ExtendedTo { get; init; }

    /// <summary>Confirm or not confirm: the decision's date (default today); a confirmation takes effect on it.</summary>
    public DateOnly? Date { get; init; }

    /// <summary>Confirm: a reason code of the confirmation list (default by date: <c>probation_completed</c> or <c>early_confirmation</c>).</summary>
    public string? ReasonCode { get; init; }

    public string? Notes { get; init; }

    /// <summary>Extend and not confirm: the probation record's row version; confirm: the employment's.</summary>
    public long? RowVersion { get; init; }
}

/// <summary>Contracts and probation of employments (slice S1.20c Build item 13).</summary>
public interface IEmploymentTermsService
{
    Task<PeopleResult> CreateContractAsync(Guid employmentId, ContractRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> RenewContractAsync(Guid contractId, ContractRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ExtendProbationAsync(Guid employmentId, ProbationDecisionRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ConfirmProbationAsync(Guid employmentId, ProbationDecisionRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> NotConfirmProbationAsync(Guid employmentId, ProbationDecisionRequest request, CancellationToken cancellationToken);
}
