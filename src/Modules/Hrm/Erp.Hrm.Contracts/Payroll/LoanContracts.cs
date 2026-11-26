namespace Erp.Hrm.Contracts.Payroll;

/// <summary>
/// Permissions of the loan services (slice S1.41, CR-097). The generated writes of the four loan tables are closed, so their actions are declared by hand in
/// <c>Erp.Hrm.Infrastructure/Access/permissions.json</c> (the codes <c>hrm.loan_types.create</c>, <c>.update</c> and <c>hrm.loans.create</c> keep the names the generated
/// ones had). Reads stay the generated <c>&lt;table&gt;.read</c>; amounts also need the salary field class (GHR-06).
/// </summary>
public static class LoanPermissions
{
    public const string TypesRead = "hrm.loan_types.read";

    /// <summary>Create a loan or advance type (FR-HRM-LON-01).</summary>
    public const string TypesCreate = "hrm.loan_types.create";

    /// <summary>Change a loan type or retire it (FR-HRM-LON-01).</summary>
    public const string TypesUpdate = "hrm.loan_types.update";

    public const string LoansRead = "hrm.loans.read";

    /// <summary>Apply for a loan for oneself or, by data scope, for an employee (HR on behalf; FR-HRM-LON-02), and see the schedule before applying.</summary>
    public const string Apply = "hrm.loans.create";

    /// <summary>Cancel an approved loan that was not disbursed (FR-HRM-LON-02).</summary>
    public const string Cancel = "hrm.loans.cancel";

    /// <summary>Record the disbursement of an approved loan with its date and payment reference (FR-HRM-LON-07). Sensitive: it opens the receivable.</summary>
    public const string Disburse = "hrm.loans.disburse";

    /// <summary>Record a repayment received outside payroll, an extra one or a full settlement, and send it for approval (FR-HRM-LON-03). Sensitive.</summary>
    public const string Receipt = "hrm.loans.receipt";

    /// <summary>Approve or reject a held repayment as the second person (FR-XM-32: the person who entered it never does). Sensitive.</summary>
    public const string ReceiptApprove = "hrm.loans.receipt_approve";

    /// <summary>The outstanding-loans and reconciliation reports (FR-HRM-LON-05, LON-07).</summary>
    public const string ReportsRead = "hrm.loan_reports.read";

    /// <summary>Every code declared in permissions.json for the loan services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [TypesCreate, TypesUpdate, Apply, Cancel, Disburse, Receipt, ReceiptApprove, ReportsRead];
}

/// <summary>Events of the loan services in <c>hrm.outbox_messages</c> for Finance (slice S1.41 Q5; D-43: no ledger here, an export and a reconciliation report only).</summary>
public static class LoanEvents
{
    public const string AggregateType = "hrm.loan";

    /// <summary>
    /// A loan was paid out (FR-HRM-LON-07): {loanId, loanNo, companyId, employmentId, loanTypeId, kind, principal, currencyCode, disbursedOn, reference, installments}.
    /// Amounts are salary-class data: the outbox is read by Finance, not by screens.
    /// </summary>
    public const string Disbursed = "hrm.loan.disbursed";

    /// <summary>
    /// An installment was recovered through a locked pay run, or a repayment was received (reduces the receivable):
    /// {loanId, loanNo, companyId, employmentId, kind, principalAmount, interestAmount, currencyCode, payRunId, installmentId, txnDate}.
    /// </summary>
    public const string InstallmentRecovered = "hrm.loan.installment_recovered";

    /// <summary>The document type of an application (workflow.json).</summary>
    public const string ApplicationDocumentType = "hrm.loan_application";

    /// <summary>The held change (maker-checker) of a repayment received outside payroll.</summary>
    public const string ReceiptKind = "hrm.loan.receipt";
}

/// <summary>
/// A loan or advance that still has something to repay, as the clearance and the final settlement ask for it (FR-HRM-LON-05, FR-XM-29; S1.25, S1.43).
/// <c>PrincipalOutstanding</c> is what the ledger says is still lent (disbursed less recovered); <c>RepayableScheduled</c> is principal and interest of the
/// installments not yet recovered; <c>NextDue</c> is the start of the pay period of the next one.
/// </summary>
public sealed record OutstandingLoan(
    Guid LoanId,
    string LoanNo,
    Guid EmploymentId,
    string TypeCode,
    string Kind,
    string CurrencyCode,
    decimal PrincipalOutstanding,
    decimal RepayableScheduled,
    int InstallmentsLeft,
    DateOnly? NextDue);

/// <summary>
/// The loans an employee still owes (slice S1.41 Build item 5, FR-HRM-LON-05): clearance (S1.25) lists them and final settlement (S1.43) blocks on them (FR-XM-29)
/// and recovers them with ledger entries of kind <c>settlement_recovery</c>. An in-process answer to a module's own code: amounts are salary-class data, so the caller
/// shows them only to people who may see them. A loan counts while it is approved and not disbursed, or active with a balance above zero.
/// </summary>
public interface IOutstandingLoans
{
    /// <summary>The open loans of each employment asked, as of the ledger now. Nothing for an employment without any.</summary>
    Task<IReadOnlyList<OutstandingLoan>> ForAsync(IReadOnlyCollection<Guid> employmentIds, CancellationToken cancellationToken);
}

/// <summary>
/// What the posting of a locked run did: <c>Recovered</c> installments marked deducted (each with a ledger entry and an event), <c>Skipped</c> installments of the period
/// that no result line took (no pay that period; they carry forward to the next run) and <c>Settled</c> loans whose balance reached zero.
/// </summary>
public sealed record LoanPostingResult(int Recovered, int Skipped, int Settled);

/// <summary>
/// The write of the loan ledger when a pay run locks (slice S1.41 Build item 4, FR-HRM-LON-03, LON-07; the data design says "recovery entries are written only when the
/// pay run locks"). S1.38's lock calls it in the locking transaction; it is idempotent (the ledger's unique key refuses a second entry for one installment). It refuses a run that is
/// not locked.
/// </summary>
public interface ILoanRecoveryPosting
{
    /// <summary>Posts the installments the run's calculation used (their result lines are in the run), skips those of the run's period that no line took.</summary>
    Task<LoanPostingResult> PostRunAsync(Guid payRunId, CancellationToken cancellationToken);
}
