using System.Data.Common;
using Erp.BuildingBlocks.Application.Tenancy;

namespace Erp.Kernel.Application.ControlPlane.Tenancy;

/// <summary>
/// The per-request (scoped) binding to one customer. It is bound once, by the resolution middleware or by the
/// migrator; binding twice is refused so no code path can switch customers in the middle of a request.
/// </summary>
public sealed class TenantContext : ITenantContext, ITenantConnectionProvider, ICurrentLicence
{
    private static readonly IReadOnlySet<string> NoModules = new HashSet<string>(StringComparer.Ordinal);
    private ResolvedTenant? _tenant;
    private DbDataSource? _dataSource;
    private LicenceSnapshot? _licence;

    public bool IsResolved => _tenant is not null;

    public string TenantCode => Tenant.Code;

    public TenantAccessMode AccessMode { get; private set; } = TenantAccessMode.NotFound;

    public IReadOnlySet<string> InstalledModules => _tenant?.InstalledModules ?? NoModules;

    /// <summary>The control-plane record of the bound customer.</summary>
    public ResolvedTenant Tenant => _tenant ?? throw new InvalidOperationException("No customer is bound to this request.");

    public DbDataSource DataSource => _dataSource ?? throw new InvalidOperationException("No customer database is bound to this request.");

    /// <summary>The licence the request is served under (slice S1.09b); bound with the customer.</summary>
    public LicenceSnapshot Licence => _licence ?? throw new InvalidOperationException("No licence is bound to this request.");

    public void Bind(ResolvedTenant tenant, TenantAccessMode accessMode, DbDataSource? dataSource, LicenceSnapshot? licence = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (_tenant is not null)
        {
            throw new InvalidOperationException("The request is already bound to a customer.");
        }

        _tenant = tenant;
        AccessMode = accessMode;
        _dataSource = dataSource;
        _licence = licence;
    }
}
