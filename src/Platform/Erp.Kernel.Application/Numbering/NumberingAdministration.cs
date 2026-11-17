namespace Erp.Kernel.Application.Numbering;

/// <summary>Body of <c>POST /api/cfg/numbering/schemes</c> (slice S1.08 Q8, Q11).</summary>
public sealed record CreateSchemeRequest(Guid? CompanyId, Guid? BranchId, string? DocumentType, string? Pattern, string? ResetRule, bool? GapFree, long? StartValue);

/// <summary>Body of <c>PUT /api/cfg/numbering/schemes/{id}</c>: allowed only while the scheme has issued no number (Q8).</summary>
public sealed record EditSchemeRequest(Guid? BranchId, string? Pattern, string? ResetRule, bool? GapFree, long? StartValue, long? RowVersion);

/// <summary>Body of <c>POST /api/cfg/numbering/schemes/{id}/retire</c>.</summary>
public sealed record RetireSchemeRequest(long? RowVersion);

/// <summary>A scheme as the numbering endpoints return it.</summary>
public sealed record SchemeView(
    Guid Id, Guid? CompanyId, Guid? BranchId, string DocumentType, string Pattern, string ResetRule, bool GapFree, long StartValue,
    string Status, bool InUse, long RowVersion);

/// <summary>One registered document type, and for the asked company whether an active scheme exists (S1.15, S1.18 readiness).</summary>
public sealed record DocumentTypeView(
    string Code, string Module, string Assign, bool GapFreeAllowed, bool OverrideAllowed, bool TenantWide, bool HasBranch,
    string DefaultPattern, string DefaultReset, bool? HasActiveScheme);

/// <summary>Numbers issued in one period of a series (Q10). <see cref="Count"/> only for gap-free series: gap-allowed ones may skip numbers.</summary>
public sealed record IssuedRange(string Period, string FirstNumber, string LastNumber, long? Count, bool GapFree);

/// <summary>One voided number (Q10).</summary>
public sealed record VoidLine(Guid Id, string Number, string Reason, Guid? DocumentId, Guid SchemeId, DateTimeOffset VoidedAt, Guid VoidedBy);

/// <summary>The void report of one scheme's series for a date range (FR-KRN-NUM-05, Q10).</summary>
public sealed record VoidReport(Guid SchemeId, string DocumentType, string Pattern, bool GapFree, DateOnly? From, DateOnly? To, IReadOnlyList<IssuedRange> Issued, IReadOnlyList<VoidLine> Voids);

/// <summary>
/// Numbering administration (slice S1.08 build items 3 and 5; FR-KRN-NUM-01, 05). Errors are localization keys:
/// <c>ArgumentException</c> with <c>errors.numbering.*</c> for refused input, <c>KeyNotFoundException</c> for a scheme the
/// caller cannot see, data exceptions for conflicts.
/// </summary>
public interface INumberingAdministration
{
    Task<SchemeView> CreateAsync(CreateSchemeRequest request, CancellationToken cancellationToken);

    Task<SchemeView> EditAsync(Guid id, EditSchemeRequest request, CancellationToken cancellationToken);

    Task<SchemeView> RetireAsync(Guid id, RetireSchemeRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentTypeView>> DocumentTypesAsync(Guid? companyId, CancellationToken cancellationToken);

    Task<VoidReport> VoidReportAsync(Guid schemeId, DateOnly? fromDay, DateOnly? toDay, CancellationToken cancellationToken);
}
