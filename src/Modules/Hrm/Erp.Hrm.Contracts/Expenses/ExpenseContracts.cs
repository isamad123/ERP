using System.Data.Common;

namespace Erp.Hrm.Contracts.Expenses;

/// <summary>
/// Permissions of expense claims (slice S1.42, CR-098; FR-HRM-EXP-02, EXP-03, GHR-06, FR-XM-32). The generated writes of <c>hrm.expense_claims</c> and
/// <c>hrm.expense_claim_lines</c> are closed: the claim service writes them and the workflow decides them. Who a claim may be made for is the role's
/// data scope (an employee for themselves, a manager for their team, HR for the company); a claim made for another person records the acting user.
/// Reads stay the generated <c>&lt;table&gt;.read</c>; categories and exchange rates stay generated master data.
/// </summary>
public static class ExpensePermissions
{
    /// <summary>Make, change, submit and cancel claims (inside the data scope of the grant).</summary>
    public const string Create = "hrm.expense_claims.create";

    /// <summary>Record the decision on each line of a claim waiting for the caller's approval (the approval task itself decides the claim). Sensitive.</summary>
    public const string Decide = "hrm.expense_claims.decide";

    public const string Read = "hrm.expense_claims.read";

    /// <summary>The hand-written codes (declared in permissions.json; kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [Create, Decide];
}

/// <summary>Events, owner types and document types of expense claims (slice S1.42).</summary>
public static class ExpenseEvents
{
    /// <summary>Written to the outbox when a claim is approved (FR-HRM-EXP-06): totals by category and coding, for Finance later (D-43).</summary>
    public const string ClaimApproved = "hrm.expense_claim.approved";

    public const string AggregateType = "hrm.expense_claim";

    public const string DocumentType = "hrm.expense_claim";

    /// <summary>Owner type of the receipt files of a claim (<c>IAttachmentOwner</c>, CR-114).</summary>
    public const string OwnerType = "hrm.expense_claims";

    /// <summary>Source type of the payroll input written for a claim (<c>hrm.pay_inputs.source_type</c>).</summary>
    public const string PayInputSource = "expense_claim";

    /// <summary>Numbering type of claims.</summary>
    public const string NumberType = "hrm.expense_claim_no";
}

/// <summary>The policy flags a line can carry (FR-HRM-EXP-03; the values of <c>hrm.expense_claim_lines.policy_flags</c>).</summary>
public static class ExpenseFlags
{
    public const string OverLimit = "over_limit";

    public const string Duplicate = "duplicate";

    public const string MissingReceipt = "missing_receipt";

    /// <summary>Outside the claim window (<c>hrm.exp.claim_window_days</c>) or dated in the future.</summary>
    public const string OutOfPolicy = "out_of_policy";

    public static IReadOnlyList<string> All { get; } = [OverLimit, Duplicate, MissingReceipt, OutOfPolicy];
}

/// <summary>The way a claim is paid (<c>hrm.expense_claims.reimburse_via</c>).</summary>
public static class ExpenseReimbursement
{
    public const string Payroll = "payroll";

    public const string PaymentBatch = "payment_batch";
}

/// <summary>A claim that is not finished: still a draft (not submitted) or submitted or approved but not yet paid (FR-XM-29).</summary>
public sealed record OpenExpenseClaim(Guid ClaimId, string ClaimNo, string Status, string PaymentStatus);

/// <summary>
/// The open claims of an employment for clearance and final settlement (FR-XM-29, slice S1.43). In-process; no permission check (the caller has its own);
/// amounts are not part of the answer (GHR-06).
/// </summary>
public interface IExpenseOpenClaims
{
    /// <summary>Claims of the employment that are drafts, or submitted or approved and not paid, oldest first.</summary>
    Task<IReadOnlyList<OpenExpenseClaim>> OpenForAsync(Guid employmentId, CancellationToken cancellationToken);
}

/// <summary>What a payment says about a claim (FR-HRM-EXP-06): paid or failed, with the payment's own reference.</summary>
public sealed record ExpensePaymentReturn(Guid ClaimId, string Status, string? Reference, DateTimeOffset At);

/// <summary>
/// The payment's answer to a claim (FR-HRM-EXP-06): the pay run that pays a claim (S1.38) or the payment batch (S1.40a) calls this in the transaction
/// that records the payment. Paid marks the claim <c>reimbursed</c>; failed keeps it approved with payment status <c>failed</c>, so it can be paid
/// again. A claim that was not approved or whose payment already ended is refused (<c>false</c>); the same answer twice changes nothing.
/// </summary>
public interface IExpensePaymentReturn
{
    Task<bool> RecordAsync(ExpensePaymentReturn answer, DbTransaction? transaction, CancellationToken cancellationToken);
}
