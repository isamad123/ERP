namespace Erp.Kernel.Contracts.Org;

/// <summary>The kinds of organization record a business module may name by id.</summary>
public enum OrgRecordKind
{
    OrgUnit,
    Branch,
    Location,
    Company,
}

/// <summary>An organization record a module named: its company (a company's own id for a company), code, names in each language, and whether it is active (not deleted).</summary>
public sealed record OrgRecord(Guid Id, OrgRecordKind Kind, Guid CompanyId, string Code, IReadOnlyDictionary<string, string> Name, bool IsActive);

/// <summary>
/// Questions a business module asks about the organization structure without reading kernel tables (D-10; slice S1.23a, contract
/// <c>IOrgStructure</c>): the units below a unit on a date (reporting hierarchies, as the subtree data scope reads them), and the
/// records behind ids. The policy framework uses both for rules on "a unit and its sub-units" and to show rules in words.
/// </summary>
public interface IOrgStructure
{
    /// <summary>For each root, the root and every unit below it in the reporting hierarchies on the date (each root appears as a key).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> SubtreesAsync(IReadOnlyCollection<Guid> roots, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>The records of the given ids that exist (soft-deleted ones are left out); unknown ids are simply missing from the answer.</summary>
    Task<IReadOnlyList<OrgRecord>> FindAsync(OrgRecordKind kind, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
