using System.Data.Common;

namespace Erp.BuildingBlocks.Application.Tenancy;

/// <summary>
/// What the current customer may do right now (FR-KRN-TEN-09). Derived on the server from the tenant status, its
/// database status and the entitlement state; never from request input.
/// </summary>
public enum TenantAccessMode
{
    /// <summary>No customer is bound to the request (unknown host, or a deleted or retired customer).</summary>
    NotFound,

    /// <summary>The customer exists but cannot be served now (being provisioned, offboarding, or its database is in maintenance).</summary>
    Unavailable,

    /// <summary>Suspended customer or locked entitlement: business data is not served.</summary>
    Locked,

    /// <summary>Read-only entitlement: reads are served, changes are refused.</summary>
    ReadOnly,

    /// <summary>Normal operation.</summary>
    Full,
}

/// <summary>
/// The one customer (tenant) a request or job is bound to (D-02, D-47, FR-KRN-TEN-04). Set once per request by the
/// tenant resolution middleware from the host name, or by the migrator and provisioning for the database they work on.
/// Business code never chooses a tenant; it only reads this.
/// </summary>
public interface ITenantContext
{
    bool IsResolved { get; }

    /// <summary>Stable customer code (also used in host names and database names).</summary>
    string TenantCode { get; }

    TenantAccessMode AccessMode { get; }

    /// <summary>Modules whose migrations are installed in this customer's database (kernel, hrm, ...).</summary>
    IReadOnlySet<string> InstalledModules { get; }
}

/// <summary>
/// The pooled connection source of the current customer's own database (FR-KRN-TEN-03). There is no way to ask for
/// another customer's database through this interface.
/// </summary>
public interface ITenantConnectionProvider
{
    /// <summary>The data source of the bound customer. Throws if no customer is bound.</summary>
    DbDataSource DataSource { get; }
}

/// <summary>
/// Secrets (database passwords, keys) kept outside the repository and outside the control-plane tables, which hold only
/// references (<c>app_secret_ref</c>, <c>admin_secret_ref</c>).
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string reference, CancellationToken cancellationToken);

    Task PutAsync(string reference, string value, CancellationToken cancellationToken);
}
