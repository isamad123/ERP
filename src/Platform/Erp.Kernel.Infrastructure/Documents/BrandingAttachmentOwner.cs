using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// The login logo (slice S1.15a, FR-ADM-SEC-08): one fixed record per customer. Whoever may read the customer's settings may read
/// the file; whoever may change them (for every company) may upload or replace it. The file belongs to the first company only
/// because every file row names one; branding itself is customer-wide.
/// </summary>
public sealed class BrandingAttachmentOwner(ITenantConnectionProvider tenant, ICallerAccess caller) : IAttachmentOwner
{
    public string OwnerType => BrandingKeys.OwnerType;

    public async Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        if (ownerId != BrandingKeys.OwnerId)
        {
            return null;
        }

        await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select id from org.companies where not is_deleted order by created_at, id limit 1", c);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid company)
        {
            return null;
        }

        var read = await caller.CoversCompanyAsync(SettingsPermissions.Read, null, cancellationToken).ConfigureAwait(false);
        var write = await caller.CoversCompanyAsync(SettingsPermissions.Update, null, cancellationToken).ConfigureAwait(false);
        return new AttachmentOwnerAccess(company, read, write, false);
    }
}
