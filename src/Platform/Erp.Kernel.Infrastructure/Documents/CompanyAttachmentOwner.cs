using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.Documents;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Files on a company (slice S1.14a Build item 1, FR-ADM-ORG-01: the logo): anyone who may read the company may read its
/// files, anyone who may change the company may add or replace them. The roles must cover the company itself. Nothing on a
/// company is sensitive.
/// </summary>
public sealed class CompanyAttachmentOwner(ITenantConnectionProvider tenant, ICallerAccess caller) : IAttachmentOwner
{
    public const string Type = "org.companies";

    public string OwnerType => Type;

    public async Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select exists (select 1 from org.companies where id = $1 and not is_deleted)", c);
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return null;
        }

        var read = await caller.CoversCompanyAsync("org.companies.read", ownerId, cancellationToken).ConfigureAwait(false);
        var write = await caller.CoversCompanyAsync("org.companies.update", ownerId, cancellationToken).ConfigureAwait(false);
        return new AttachmentOwnerAccess(ownerId, read, write, read);
    }
}
