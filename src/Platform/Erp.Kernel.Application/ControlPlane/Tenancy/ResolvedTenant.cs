namespace Erp.Kernel.Application.ControlPlane.Tenancy;

/// <summary>
/// A customer as the control plane knows it: registry row, its production database, its stored entitlements (current and
/// scheduled; the state is worked out from their dates, slice S1.09b) and the modules installed in its database.
/// </summary>
public sealed record ResolvedTenant(
    Guid TenantId,
    string Code,
    string Status,
    Guid TenantDatabaseId,
    string DatabaseName,
    string DatabaseStatus,
    string ClusterHost,
    int ClusterPort,
    string AppSecretRef,
    IReadOnlyList<EntitlementRecord> Entitlements,
    IReadOnlySet<string> InstalledModules);

/// <summary>Looks customers up in the control plane (cp.tenant_domains, FR-KRN-TEN-04).</summary>
public interface ITenantDirectory
{
    /// <summary>The customer that owns <paramref name="normalizedHost"/>, or null when no customer does.</summary>
    ValueTask<ResolvedTenant?> FindByHostAsync(string normalizedHost, CancellationToken cancellationToken);
}

/// <summary>The pooled data source of a customer's own database, as the application login (app_rw).</summary>
public interface ITenantDataSourceFactory
{
    ValueTask<System.Data.Common.DbDataSource> GetAsync(ResolvedTenant tenant, CancellationToken cancellationToken);
}
