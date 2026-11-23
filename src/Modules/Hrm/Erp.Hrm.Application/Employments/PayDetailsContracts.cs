using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Employments;

/// <summary>
/// One change of an employment's payment accounts (slice S1.20 Build item 14, FR-HRM-EMP-06, FR-HRM-PAY-24). A request carries one
/// or more of them, checked and applied together (held for a second person while <c>hrm.bank_accounts.requires_approval</c> is on),
/// so the remainder can move from one account to another in one step.
/// </summary>
public sealed class BankAccountChangeInput
{
    /// <summary><c>add</c> (a new account), <c>change</c> (from <see cref="EffectiveFrom"/>), <c>end</c> (on <see cref="ValidTo"/>) or <c>remove</c>.</summary>
    public string? Action { get; init; }

    /// <summary>The account to change, end or remove.</summary>
    public Guid? Id { get; init; }

    /// <summary>The account's row version the caller read (change, end, remove).</summary>
    public long? RowVersion { get; init; }

    /// <summary><c>bank_transfer</c>, <c>cheque</c>, <c>cash</c> or <c>mobile_wallet</c>.</summary>
    public string? PaymentMode { get; init; }

    /// <summary>A bank (or wallet provider) of <c>ref.banks</c>; needed for a bank transfer or mobile wallet.</summary>
    public Guid? BankId { get; init; }

    public string? AccountTitle { get; init; }

    /// <summary>The account number as typed; on a change, null keeps the stored one.</summary>
    public string? AccountNumber { get; init; }

    /// <summary>The IBAN as typed (spaces allowed); on a change, null keeps the stored one.</summary>
    public string? Iban { get; init; }

    /// <summary>On a change: remove the stored IBAN (an account number must remain for a bank transfer).</summary>
    public bool? ClearIban { get; init; }

    /// <summary>On a change: remove the stored account number (an IBAN must remain for a bank transfer).</summary>
    public bool? ClearAccountNumber { get; init; }

    /// <summary><c>remainder</c> (what is left after the others), <c>percent</c> or <c>amount</c>.</summary>
    public string? SplitKind { get; init; }

    /// <summary>The percent (above 0, at most 100) or the amount (above 0) of net pay; none for the remainder.</summary>
    public decimal? SplitValue { get; init; }

    /// <summary>Order of payment, 1 to 99, unique on every date among the employment's accounts.</summary>
    public int? Priority { get; init; }

    /// <summary>Add: the first day (default: today, or the joining date when later).</summary>
    public DateOnly? ValidFrom { get; init; }

    /// <summary>Add: the day the account stops being used (open when empty). End: that day.</summary>
    public DateOnly? ValidTo { get; init; }

    /// <summary>Change: the first day of the new details (default: the account's own first day, which corrects the whole row).</summary>
    public DateOnly? EffectiveFrom { get; init; }
}

/// <summary>A request to change an employment's payment accounts.</summary>
public sealed class BankAccountsRequest
{
    public IReadOnlyList<BankAccountChangeInput>? Changes { get; init; }

    /// <summary>Why, shown to the approver.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The answer to the duplicate warning (the same account on another person, the "ghost employee" check; FR-HRM-EMP-18):
    /// <c>distinct</c> (they are different people; needs <c>hrm.person_duplicate_cases.update</c>) or <c>review</c> (a review case).
    /// </summary>
    public string? Duplicates { get; init; }
}

/// <summary>A new statutory registration of an employment (slice S1.20 Build item 15). Registrations only: no contribution rule.</summary>
public sealed class StatutoryRegistrationRequest
{
    /// <summary>The scheme's code (for example <c>eobi</c>); typed until the pack catalogue lists the schemes (S1.23).</summary>
    public string? Scheme { get; init; }

    public bool? IsApplicable { get; init; }

    /// <summary>Required when the scheme does not apply; none when it does.</summary>
    public string? ExemptionReason { get; init; }

    /// <summary>The registration number as typed; on a change, null keeps the stored one.</summary>
    public string? RegistrationNo { get; init; }

    /// <summary>On a change: remove the stored registration number.</summary>
    public bool? ClearRegistrationNo { get; init; }

    public DateOnly? ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }

    /// <summary>On a change: remove the stored last day (the registration stays open).</summary>
    public bool? ClearValidTo { get; init; }

    /// <summary>A change: the first day of the new details (default: the row's own first day, which corrects it).</summary>
    public DateOnly? EffectiveFrom { get; init; }

    /// <summary>A change: the row version the caller read.</summary>
    public long? RowVersion { get; init; }
}

/// <summary>The payment accounts of employments (slice S1.20d Build item 14). Answers as for the other employment services.</summary>
public interface IEmployeeBankAccountService
{
    /// <summary>The employment's accounts (valid on <paramref name="asOf"/>, else all), the numbers masked.</summary>
    Task<PeopleResult> ListAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken);

    /// <summary>The full account number and IBAN of one account, with the reveal permission; access-logged (GHR-02).</summary>
    Task<PeopleResult> RevealAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Checks the changes and applies them, or holds them for a second person.</summary>
    Task<PeopleResult> ChangeAsync(Guid employmentId, BankAccountsRequest request, CancellationToken cancellationToken);
}

/// <summary>The statutory registrations of employments (slice S1.20d Build item 15).</summary>
public interface IStatutoryRegistrationService
{
    Task<PeopleResult> ListAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken);

    Task<PeopleResult> RevealAsync(Guid registrationId, CancellationToken cancellationToken);

    Task<PeopleResult> CreateAsync(Guid employmentId, StatutoryRegistrationRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid registrationId, StatutoryRegistrationRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> DeleteAsync(Guid registrationId, long? rowVersion, CancellationToken cancellationToken);
}
