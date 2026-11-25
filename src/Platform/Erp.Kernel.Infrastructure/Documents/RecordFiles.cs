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

    public async Task<IReadOnlyDictionary<Guid, string>> ChecksumsAsync(IReadOnlyCollection<Guid> fileIds, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        const string Sql = "select f.id, f.checksum from doc.files f where f.id = any($1) and f.status in ('clean', 'uploading') and f.archived_at is null";
        var ids = fileIds.Distinct().ToArray();
        IReadOnlyList<(Guid Id, string Checksum)> rows;
        if (transaction is NpgsqlTransaction tx)
        {
            rows = await Notifications.NotificationConsumer.ReadAsync(tx.Connection!, tx, Sql, r => (r.GetGuid(0), r.GetString(1)), cancellationToken, ids).ConfigureAwait(false);
        }
        else
        {
            await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            rows = await Notifications.NotificationConsumer.ReadAsync(c, null, Sql, r => (r.GetGuid(0), r.GetString(1)), cancellationToken, ids).ConfigureAwait(false);
        }

        return rows.ToDictionary(r => r.Id, r => r.Checksum);
    }
}
