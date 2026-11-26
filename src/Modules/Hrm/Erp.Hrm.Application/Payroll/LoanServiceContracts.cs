using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Payroll;

/// <summary>A loan type as written (slice S1.41 Build item 1, FR-HRM-LON-01). <see cref="RowVersion"/> is required to change one.</summary>
public sealed record LoanTypeRequest(
    Guid? CompanyId,
    string? Code,
    IReadOnlyDictionary<string, string>? NameI18n,
    string? Kind,
    Guid? PolicyId,
    Guid? DeductionComponentId,
    bool? IsActive,
    long? RowVersion);

/// <summary>The loan types service: create, change and retire (switch off); a type is never deleted.</summary>
public interface ILoanTypeService
{
    Task<PeopleResult> CreateAsync(LoanTypeRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid typeId, LoanTypeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// An application for a loan, or its preview (FR-HRM-LON-02). The employment is the caller's own when it is not named (by id or, for HR, by employee number); the guarantor is named by id
/// or by employee number (the same company); the amount is salary-class data.
/// </summary>
public sealed record LoanApplicationRequest(
    Guid? EmploymentId,
    Guid? LoanTypeId,
    decimal? Principal,
    int? Installments,
    Guid? StartPeriodId,
    Guid? GuarantorEmploymentId,
    string? Reason,
    string? EmployeeNo = null,
    string? GuarantorEmployeeNo = null);

/// <summary>The date and payment reference of a disbursement (Q3: recorded by hand; S1.40 payment batches may carry it later).</summary>
public sealed record LoanDisbursementRequest(DateOnly? Date, string? Reference);

/// <summary>A repayment received outside payroll: an extra one, or the settlement of everything outstanding (Build item 5).</summary>
public sealed record LoanReceiptRequest(decimal? Amount, DateOnly? Date, string? Reference, bool? FullSettlement, string? Reason);

/// <summary>The loan services (slice S1.41 Build items 2, 3 and 5).</summary>
public interface ILoanService
{
    /// <summary>Checks the application and works out its schedule without saving anything.</summary>
    Task<PeopleResult> PreviewAsync(LoanApplicationRequest request, CancellationToken cancellationToken);

    /// <summary>Saves the application, numbers it and sends it for approval.</summary>
    Task<PeopleResult> ApplyAsync(LoanApplicationRequest request, CancellationToken cancellationToken);

    /// <summary>A loan with its type, schedule, ledger and balance, under the caller's field rules.</summary>
    Task<PeopleResult> DetailAsync(Guid loanId, CancellationToken cancellationToken);

    /// <summary>Cancels an approved loan that was not disbursed.</summary>
    Task<PeopleResult> CancelAsync(Guid loanId, CancellationToken cancellationToken);

    /// <summary>Records the payout of an approved loan.</summary>
    Task<PeopleResult> DisburseAsync(Guid loanId, LoanDisbursementRequest request, CancellationToken cancellationToken);

    /// <summary>Holds a received repayment for a second person.</summary>
    Task<PeopleResult> ReceiptAsync(Guid loanId, LoanReceiptRequest request, CancellationToken cancellationToken);
}

/// <summary>The loan reports (Build item 6, FR-HRM-LON-05, LON-07).</summary>
public interface ILoanReportService
{
    /// <summary>The open loans in the caller's data scope, with what is outstanding.</summary>
    Task<PeopleResult> OutstandingAsync(Guid? companyId, int limit, int offset, CancellationToken cancellationToken);

    /// <summary>Per company: ledger total against disbursed less recovered, and the installments against the ledger; differences listed.</summary>
    Task<PeopleResult> ReconciliationAsync(Guid? companyId, CancellationToken cancellationToken);
}
