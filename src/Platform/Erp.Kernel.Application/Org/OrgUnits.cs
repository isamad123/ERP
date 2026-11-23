using System.Text.Json.Nodes;
using Erp.Kernel.Application.Access;

namespace Erp.Kernel.Application.Org;

/// <summary>Body of the move of an organization unit (slice S1.14b Build item 9, Q9). The reason is optional and kept in the change log.</summary>
public sealed record MoveUnitRequest(Guid? HierarchyId, Guid? NewParentId, DateOnly? EffectiveFrom, string? Reason);

/// <summary>
/// One unit of a hierarchy on one date. <see cref="ParentUnitId"/> is null for a root; <see cref="LinkId"/> is the link row
/// that holds the parent on that date and <see cref="ValidFrom"/>/<see cref="ValidTo"/> its period (the end is exclusive).
/// </summary>
public sealed record OrgTreeNode(
    Guid UnitId, Guid? ParentUnitId, string Code, JsonNode? Name, Guid UnitTypeId, Guid CompanyId, Guid? LocationId, bool IsActive,
    Guid? LinkId, DateOnly? ValidFrom, DateOnly? ValidTo);

/// <summary>A hierarchy as it stood on <see cref="AsOf"/>: the placed units, and the active units not in this hierarchy on that date.</summary>
public sealed record OrgTree(Guid HierarchyId, DateOnly AsOf, IReadOnlyList<OrgTreeNode> Nodes, IReadOnlyList<OrgTreeNode> Unplaced);

/// <summary>A unit that differs between two dates; the parents are null for a root (or for "not in the hierarchy" on that side).</summary>
public sealed record OrgCompareChange(Guid UnitId, string Code, JsonNode? Name, Guid? FromParentId, string? FromParentCode, Guid? ToParentId, string? ToParentCode);

/// <summary>Units added to, removed from and moved inside a hierarchy between two dates (slice S1.14b Build item 10, FR-ADM-ORG-03).</summary>
public sealed record OrgCompare(Guid HierarchyId, DateOnly From, DateOnly To, IReadOnlyList<OrgCompareChange> Added, IReadOnlyList<OrgCompareChange> Removed, IReadOnlyList<OrgCompareChange> Moved);

/// <summary>
/// Organization units over time (slice S1.14b, CR-070): the structure of a hierarchy on a date, the difference between two
/// dates, and the move of a unit to another parent from a date, in one transaction and audited.
/// </summary>
public interface IOrgUnitService
{
    Task<(AccessResult Result, OrgTree? Tree)> TreeAsync(Guid hierarchyId, DateOnly? asOf, CancellationToken cancellationToken);

    Task<(AccessResult Result, OrgCompare? Compare)> CompareAsync(Guid hierarchyId, DateOnly? firstDate, DateOnly? secondDate, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the unit's current link in the hierarchy on the effective date and starts one under the new parent (null: a root).
    /// The old parent still applies the day before. Refuses a cycle, a date on or before the start of the current link, the
    /// same parent, and a parent of another company.
    /// </summary>
    Task<AccessResult> MoveAsync(Guid unitId, MoveUnitRequest request, CancellationToken cancellationToken);
}
