using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Itg;
using Erp.Kernel.Infrastructure.Documents;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// Keeps finished export files (slice S1.16b, FR-ADM-DAT-04): encrypted with the customer's file key in the file store, a sensitive row of
/// <c>doc.files</c> owned by the export with a retention date (<c>itg.export.keep_days</c>), read back only for the export that owns the file.
/// The file is made by the server, so it is stored as clean without the virus scan. The expiry job deletes the content when the date passes.
/// </summary>
public sealed class ExportFiles(ITenantConnectionProvider tenant, ITenantContext tenantContext, IClock clock, ICurrentActor actor, IAuditContext audit, IFileStore store, FileEncryption encryption)
{
    public async Task<Guid> StoreAsync(byte[] content, string fileName, string contentType, Guid companyId, Guid exportId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        var key = $"{TenantFileKeys.Prefix(tenantContext.TenantCode)}/{id:N}";
        await store.PutAsync(key, await encryption.EncryptAsync(id, content, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        try
        {
            await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into doc.files (id, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, storage_key, version_no, document_type,
                    retention_until, is_sensitive, status, legal_hold, key_version, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, 1, 'export', $10, true, $11, false, $12, $13, $14, $13, $14, 1)
                """, cancellationToken, id, companyId, ExportCodes.OwnerType, exportId, fileName, contentType, (long)content.Length, FileValidation.Checksum(content), key,
                DateOnly.FromDateTime(expiresAt.UtcDateTime), FileStatuses.Clean, TenantFileKeys.KeyVersion, now, actor.UserId).ConfigureAwait(false);
            var row = (await FileRows.FindAsync(c, tx, id, cancellationToken).ConfigureAwait(false))!;
            await FileRows.LogAsync(c, tx, row, "create", [("file_name", null, row.FileName), ("owner_type", null, row.OwnerType), ("owner_id", null, row.OwnerId)],
                actor.UserId, audit.ActorKind, audit.Source, audit.CorrelationId, audit.IpAddress, now, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return id;
    }

    /// <summary>The decrypted content of the file of this export; a file of another record, or one already purged, is never read.</summary>
    public async Task<byte[]> ReadAsync(Guid fileId, Guid exportId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var file = await FileRows.FindAsync(c, null, fileId, cancellationToken).ConfigureAwait(false) ?? throw new ExportException("errors.export.file_missing", 404);
        if (!string.Equals(file.OwnerType, ExportCodes.OwnerType, StringComparison.Ordinal) || file.OwnerId != exportId)
        {
            throw new ExportException("errors.export.file_missing", 404);
        }

        if (file.Status != FileStatuses.Clean || file.StorageKey == FileStatuses.PurgedKey)
        {
            throw new ExportException("errors.export.expired", 410);
        }

        var stored = await store.GetAsync(file.StorageKey, cancellationToken).ConfigureAwait(false) ?? throw new ExportException("errors.export.file_missing", 404);
        var content = await encryption.DecryptAsync(file.Id, stored, cancellationToken).ConfigureAwait(false);
        if (FileValidation.Checksum(content) != file.Checksum)
        {
            throw new InvalidOperationException($"The stored content of file {file.Id} does not match its checksum.");
        }

        return content;
    }

    /// <summary>Destroys the content of an expired export's file and marks its row purged (as retention does); true when this call did it.</summary>
    public async Task<bool> PurgeAsync(Guid fileId, Guid by, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        string storageKey;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using (var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var file = await FileRows.FindAsync(c, tx, fileId, cancellationToken).ConfigureAwait(false);
            if (file is null || file.StorageKey == FileStatuses.PurgedKey || file.LegalHold)
            {
                return false;
            }

            storageKey = file.StorageKey;
            var changed = await IdentityDb.ExecuteAsync(c, tx, "update doc.files set status = 'deleted', storage_key = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1 and storage_key <> $2 and not legal_hold",
                cancellationToken, fileId, FileStatuses.PurgedKey, now, by).ConfigureAwait(false);
            if (changed != 1)
            {
                return false;
            }

            await FileRows.LogAsync(c, tx, file, "delete", [("status", file.Status, FileStatuses.Deleted), ("content", "stored", "destroyed")], by, ActorKinds.Job, AuditSources.Job, audit.CorrelationId, null, now, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await store.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
