using System.Data.Common;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Numbering;

/// <summary>
/// One numbered document type (data design key <c>numbering</c>, slice S1.08 Q1, CR-033), generated per module.
/// </summary>
/// <param name="Code">Document type code, for example <c>hrm.employee_no</c>; stored in <c>cfg.numbering_schemes.document_type</c>.</param>
/// <param name="Module">Module that owns the document.</param>
/// <param name="Table">Table holding the number.</param>
/// <param name="Column">Numbered column.</param>
/// <param name="Assign"><c>create</c> (the generated API numbers it on save) or <c>post</c> (the module's post service does).</param>
/// <param name="GapFreeAllowed">Customers may switch the series to gap-free (Q3).</param>
/// <param name="OverrideAllowed">A typed-in number may replace the automatic one (HRM-EMP-04, Q9).</param>
/// <param name="TenantWide">One series for the whole customer, schemes without a company (Q6).</param>
/// <param name="DefaultPattern">Pattern of the default scheme (Q7).</param>
/// <param name="DefaultReset">Reset rule of the default scheme (Q7).</param>
/// <param name="HasBranch">The document has a branch, so branch schemes and <c>{branch}</c> apply (Q4, P2).</param>
public sealed record DocumentType(
    string Code,
    string Module,
    string Table,
    string Column,
    string Assign,
    bool GapFreeAllowed,
    bool OverrideAllowed,
    bool TenantWide,
    string DefaultPattern,
    string DefaultReset,
    bool HasBranch);

/// <summary>A module's numbered document types (generated <c>&lt;Module&gt;NumberingTypes</c>), registered by the host.</summary>
public interface IDocumentTypeSource
{
    IReadOnlyList<DocumentType> Types { get; }
}

/// <summary>
/// What to number: the document type, its company (null only for tenant-wide types), its branch (null when it has none)
/// and its business date (Q5: the period is decided by this date; null means today in the company's time zone).
/// </summary>
public sealed record NumberRequest(string DocumentType, Guid? CompanyId, Guid? BranchId, DateOnly? Date);

/// <summary>Outcome of the manual-number check (HRM-EMP-04, Q9). <see cref="ErrorKey"/> is set when refused.</summary>
public sealed record ManualNumberCheck(bool Accepted, string? ErrorKey);

/// <summary>
/// The kernel numbering service (FR-KRN-NUM-02, 03, 06; slice S1.08 build item 2). In-process only: no number is ever
/// handed out through the API (Q11). Failures are <see cref="NumberingException"/> with a localization key.
/// </summary>
public interface INumberAllocator
{
    /// <summary>
    /// A number for a document being saved (generated create endpoints, "on create" types). Gap-allowed only: taken on
    /// its own short connection, so it never waits and a failed save simply skips it. A series switched to gap-free is
    /// refused (<c>errors.numbering.numbered_at_posting</c>): gap-free numbers are given only at posting (Q3).
    /// </summary>
    Task<string> AssignOnCreateAsync(NumberRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// A number at posting or finalizing, called by the module's post service as the last step before commit, with its
    /// open transaction. Gap-free: taken in that transaction (a rollback gives it back; another poster waits at most 5
    /// seconds, then <c>errors.numbering.busy</c>). Gap-allowed: taken outside it, like <see cref="AssignOnCreateAsync"/>.
    /// </summary>
    Task<string> AssignAsync(NumberRequest request, DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a typed-in number is safe (Q9): allowed only for override types on gap-allowed schemes, and refused when the
    /// scheme could still produce it later. Duplicates are left to the document table's unique key.
    /// </summary>
    Task<ManualNumberCheck> CheckManualAsync(NumberRequest request, string number, CancellationToken cancellationToken);
}

/// <summary>A cancelled numbered document (FR-KRN-NUM-05, FR-XM-06, Q10): it keeps its number and the number is listed as void.</summary>
public sealed record VoidRequest(string DocumentType, Guid? CompanyId, Guid? BranchId, string Number, string Reason, Guid? DocumentId);

/// <summary>Records voided numbers (Q10). Modules call it when a numbered document is cancelled, in the cancel's transaction.</summary>
public interface INumberVoids
{
    /// <returns>The void row's id. Voiding the same number twice is refused (<c>errors.numbering.already_voided</c>).</returns>
    Task<Guid> RecordAsync(VoidRequest request, DbTransaction? transaction, CancellationToken cancellationToken);
}

/// <summary>A numbering rule refused the request; <see cref="Exception.Message"/> is a localization key (<c>errors.numbering.*</c>).</summary>
public sealed class NumberingException(string errorKey) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;
}

/// <summary>Permissions of the purpose-built numbering endpoints (Q11). Read stays the generated <c>cfg.numbering_schemes.read</c>.</summary>
public static class NumberingPermissions
{
    public const string Read = "cfg.numbering_schemes.read";
    public const string Create = "cfg.numbering_schemes.create";
    public const string Update = "cfg.numbering_schemes.update";
    public const string Retire = "cfg.numbering_schemes.retire";
    public const string VoidReport = "cfg.numbering.void_report";

    /// <summary>Create and update keep their codes although the generated endpoints are closed (Q11).</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Create, "cfg", "permissions.cfg.numbering_schemes.create", false),
        new(Update, "cfg", "permissions.cfg.numbering_schemes.update", false),
        new(Retire, "cfg", "permissions.cfg.numbering_schemes.retire", false),
        new(VoidReport, "cfg", "permissions.cfg.numbering.void_report", false),
    ];
}
