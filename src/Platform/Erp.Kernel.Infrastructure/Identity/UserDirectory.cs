using System.Data.Common;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>The kernel's answers about users for other modules (slice S1.20b), read from iam.users of the bound customer.</summary>
public sealed class UserDirectory(ITenantConnectionProvider tenant) : IUserDirectory
{
    private const string Linkable = "select exists (select 1 from iam.users where id = $1 and not is_deleted and not is_system)";

    public async Task<bool> IsLinkableAsync(Guid userId, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            return await IdentityDb.ScalarAsync<bool>((NpgsqlConnection)transaction.Connection!, (NpgsqlTransaction)transaction, Linkable, cancellationToken, userId).ConfigureAwait(false);
        }

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await IdentityDb.ScalarAsync<bool>(connection, null, Linkable, cancellationToken, userId).ConfigureAwait(false);
    }
}
