using Erp.BuildingBlocks.Application.Tenancy;

namespace Erp.Kernel.Application.ControlPlane.Tenancy;

/// <summary>
/// FR-KRN-TEN-09 and the approved S1.03 plan: the access mode is the most restrictive of what the tenant status, the
/// database status and the entitlement state allow. Data is never deleted or hidden by this; only serving stops.
/// </summary>
public static class TenantAccessPolicy
{
    public static TenantAccessMode Decide(string tenantStatus, string databaseStatus, string? entitlementStatus)
    {
        var byTenant = tenantStatus switch
        {
            "active" => TenantAccessMode.Full,
            "suspended" => TenantAccessMode.Locked,
            "provisioning" or "offboarding" => TenantAccessMode.Unavailable,
            _ => TenantAccessMode.NotFound, // deleted, or a status this code does not know: fail closed
        };

        // Migrations are expand-and-contract, so a database being migrated keeps serving; a failed one is in maintenance.
        var byDatabase = databaseStatus switch
        {
            "ready" or "migrating" => TenantAccessMode.Full,
            "creating" or "maintenance" => TenantAccessMode.Unavailable,
            _ => TenantAccessMode.NotFound,
        };

        var byEntitlement = entitlementStatus switch
        {
            "active" or "warning" or "grace" => TenantAccessMode.Full,
            "read_only" => TenantAccessMode.ReadOnly,
            _ => TenantAccessMode.Locked, // locked, or no current entitlement: fail closed
        };

        return (TenantAccessMode)Math.Min(Math.Min((int)byTenant, (int)byDatabase), (int)byEntitlement);
    }
}
