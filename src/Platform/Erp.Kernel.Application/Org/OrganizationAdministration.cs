using Erp.Kernel.Application.Access;

namespace Erp.Kernel.Application.Org;

// The registration number (Value) is in full, masked (****1234) or null, as field security allows (ValueAccess: full, masked, hidden).
/// <summary>A company's tax or statutory registration as the caller may see it (slice S1.14a, FR-KRN-ORG-01, FR-ADM-ORG-01).</summary>
public sealed record RegistrationView(
    Guid Id, Guid CompanyId, string Kind, string Authority, string? Value, string ValueAccess, DateOnly ValidFrom, DateOnly? ValidTo, long RowVersion);

/// <summary>Body of a registration create or update. On update <see cref="RowVersion"/> is required.</summary>
public sealed record RegistrationRequest(string? Kind, string? Authority, string? Value, DateOnly? ValidFrom, DateOnly? ValidTo, long? RowVersion);

/// <summary>A company salary bank account as the caller may see it; account number and IBAN follow field security.</summary>
public sealed record BankAccountView(
    Guid Id, Guid CompanyId, string BankCode, string AccountTitle, string? AccountNumber, string? Iban, string AccountAccess,
    string CurrencyCode, string? SalaryFileFormat, long RowVersion);

/// <summary>
/// Body of a bank account create or update. On update a null account number or IBAN keeps the stored one;
/// <see cref="ClearIban"/> removes the IBAN. <see cref="RowVersion"/> is required on update and delete.
/// </summary>
public sealed record BankAccountRequest(
    string? BankCode, string? AccountTitle, string? AccountNumber, string? Iban, bool? ClearIban, string? CurrencyCode, string? SalaryFileFormat, long? RowVersion, string? Reason);

/// <summary>A year of holidays copied to another year (slice S1.14a Build item 5): dates move to the same day and month.</summary>
public sealed record CopyYearRequest(int? FromYear, int? ToYear);

/// <summary>The answer of a read that can be refused: <see cref="Outcome"/> says why when it is not <see cref="AccessOutcome.Done"/>.</summary>
public sealed record OrgRead<T>(AccessOutcome Outcome, IReadOnlyList<T> Items, string? Error = null);

/// <summary>Builders of <see cref="OrgRead{T}"/>.</summary>
public static class OrgReads
{
    public static OrgRead<T> Of<T>(IReadOnlyList<T> items) => new(AccessOutcome.Done, items);

    public static OrgRead<T> Refused<T>(AccessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Outcome, [], result.Error);
    }
}

/// <summary>Result of copying a year of holidays: how many were created and how many already existed.</summary>
public sealed record CopyYearResult(int Created, int Skipped);

/// <summary>
/// Administration of the organization data that the generated API cannot write (slice S1.14a, CR-070): company
/// registrations and bank accounts (encrypted values, field security, access log, maker-checker on bank accounts) and the
/// holiday calendar helpers.
/// </summary>
public interface IOrganizationAdministration
{
    Task<OrgRead<RegistrationView>> RegistrationsAsync(Guid companyId, CancellationToken cancellationToken);

    Task<AccessResult> CreateRegistrationAsync(Guid companyId, RegistrationRequest request, CancellationToken cancellationToken);

    Task<AccessResult> UpdateRegistrationAsync(Guid registrationId, RegistrationRequest request, CancellationToken cancellationToken);

    Task<OrgRead<BankAccountView>> BankAccountsAsync(Guid companyId, CancellationToken cancellationToken);

    Task<AccessResult> CreateBankAccountAsync(Guid companyId, BankAccountRequest request, CancellationToken cancellationToken);

    Task<AccessResult> UpdateBankAccountAsync(Guid bankAccountId, BankAccountRequest request, CancellationToken cancellationToken);

    Task<AccessResult> DeleteBankAccountAsync(Guid bankAccountId, BankAccountRequest request, CancellationToken cancellationToken);

    /// <summary>Copies the holidays of one year of a calendar to another year of the same calendar; existing dates are kept.</summary>
    Task<(AccessResult Result, CopyYearResult? Copied)> CopyYearAsync(Guid calendarId, CopyYearRequest request, CancellationToken cancellationToken);
}
