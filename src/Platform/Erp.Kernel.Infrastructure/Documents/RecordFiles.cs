using System.Data.Common;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Documents;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// The current files of a record (contract <see cref="IRecordFiles"/>, slice S1.32b): the head version of each attachment that is clean or still
/// waiting for its scan, not deleted and not archived. Read on the caller's transaction when one is given, otherwise on a connection of its own.
/// </summary>
public sealed class RecordFiles(ITenantConnectionProvider tenant) : IRecordFiles
{
    public async Task<IReadOnlyList<Guid>> CurrentAsync(string ownerType, Guid ownerId, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            select f.id from doc.files f
            where f.owner_type = $1 and f.owner_id = $2 and f.status in ('clean', 'uploading') and f.archived_at is null and {FileRows.IsHead}
            order by f.created_at desc, f.id
            """;
        if (transaction is NpgsqlTransaction tx)
        {
            return await Notifications.NotificationConsumer.ReadAsync(tx.Connection!, tx, Sql, r => r.GetGuid(0), cancellationToken, ownerType, ownerId).ConfigureAwait(false);
        }

        await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await Notifications.NotificationConsumer.ReadAsync(c, null, Sql, r => r.GetGuid(0), cancellationToken, ownerType, ownerId).ConfigureAwait(false);
    }
}
