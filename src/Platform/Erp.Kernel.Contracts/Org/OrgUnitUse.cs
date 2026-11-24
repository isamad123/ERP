namespace Erp.Kernel.Contracts.Org;

/// <summary>
/// A module's rule on switching an organization unit off (slice S1.22b, FR-HRM-POS-08, ruling Q6): the kernel asks every guard before a
/// unit is deactivated or deleted and refuses with the localization key a guard answers. HR answers while active positions or current
/// assignments still reference the unit; the people and positions have to be moved first. Modules reach the unit only by id (D-10).
/// </summary>
public interface IOrgUnitUseGuard
{
    /// <summary>The localization key of the reason the unit may not be switched off, or null when nothing of the module uses it.</summary>
    Task<string?> DeactivationProblemAsync(Guid unitId, CancellationToken cancellationToken);
}

/// <summary>Events of the organization structure written to the kernel outbox (slice S1.22b).</summary>
public static class OrgEvents
{
    /// <summary>A unit got a new parent (payload: unitId, companyId, hierarchyId, oldParentId, newParentId, effectiveFrom). HR turns it into <c>hrm.org_structure_changed</c>.</summary>
    public const string UnitMoved = "org.unit_moved";

    public const string AggregateType = "org.org_unit";
}
