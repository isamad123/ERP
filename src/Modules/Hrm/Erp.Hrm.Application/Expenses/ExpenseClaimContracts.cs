using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Expenses;

/// <summary>One line of a claim as sent by the claimant (slice S1.42, FR-HRM-EXP-02). <see cref="Id"/> names an existing line of the draft; a line without one is new, and a saved line not named is removed.</summary>
public sealed record ExpenseLineInput(
    Guid? Id,
    Guid? CategoryId,
    DateOnly? ExpenseDate,
    decimal? Amount,
    string? CurrencyCode,
    decimal? Quantity,
    string? Description,
    Guid? ReceiptFileId,
    string? Project,
    string? CostCentre);

/// <summary>A claim as sent by the claimant: for the caller's own employment unless one is named (and the data scope allows it), paid through payroll or a payment batch.</summary>
public sealed record ExpenseClaimInput(Guid? EmploymentId, string? Title, string? ReimburseVia, IReadOnlyList<ExpenseLineInput>? Lines, long? RowVersion, bool Submit = false);

/// <summary>An approver's decision on one line: the amount accepted (0 rejects the line) and the reason when it is less than claimed.</summary>
public sealed record ExpenseLineDecisionInput(Guid? LineId, decimal? ApprovedAmount, string? Reason);

/// <summary>The decisions of an approver on the lines of one claim (all lines of the claim are named).</summary>
public sealed record ExpenseDecisionsInput(IReadOnlyList<ExpenseLineDecisionInput>? Decisions);

/// <summary>
/// The expense claim service (slice S1.42 Build items 2 to 5; FR-HRM-EXP-02, EXP-03, EXP-05, GHR-06, FR-XM-32): drafts with lines, policy flags and
/// conversion, submit to the route (manager, then finance by amount), cancel, the detail an approver reads, the approver's line decisions and the
/// open claims of an employment (FR-XM-29). A claim is decided through the workflow engine (document type <c>hrm.expense_claim</c>); the person it is
/// for and the person who made it never approve it.
/// </summary>
public interface IExpenseClaimService
{
    Task<PeopleResult> CreateAsync(ExpenseClaimInput input, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid claimId, ExpenseClaimInput input, CancellationToken cancellationToken);

    Task<PeopleResult> SubmitAsync(Guid claimId, long? rowVersion, CancellationToken cancellationToken);

    /// <summary>A draft ends here (it keeps its number); a submitted claim is withdrawn through <c>/api/wf/instances/{id}/withdraw</c>.</summary>
    Task<PeopleResult> CancelAsync(Guid claimId, long? rowVersion, CancellationToken cancellationToken);

    Task<PeopleResult> GetAsync(Guid claimId, CancellationToken cancellationToken);

    Task<PeopleResult> DecideLinesAsync(Guid claimId, ExpenseDecisionsInput input, CancellationToken cancellationToken);

    Task<PeopleResult> OpenClaimsAsync(Guid employmentId, CancellationToken cancellationToken);
}
