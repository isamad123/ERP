using System.Globalization;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Entitlements;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Attachments of records (slice S1.11c1 Build items 17 and 18; KRN-DOC-01 to KRN-DOC-03, KRN-DOC-02, KRN-WFL-07, rulings
/// Q20 to Q23). Files follow their record: the record's owner says who may read and who may change it, and everything here
/// asks the owner first. Content is checked (size, real type), encrypted with the customer's file key and stored under the
/// customer's prefix; the row, its change-log entry and the storage-limit check go together. A download needs a clean file
/// (when scanning is required); downloads of sensitive files are in the access log. Delete hides; retention removes content.
/// </summary>
public sealed class AttachmentService(
    ITenantConnectionProvider tenant,
    ITenantContext tenantContext,
    IClock clock,
    ICurrentActor actor,
    IAuditContext audit,
    IPermissionChecker permissions,
    IEnumerable<IAttachmentOwner> owners,
    IFileStore store,
    FileEncryption encryption,
    FileScanning scanning,
    ICurrentLicence licence) : IAttachmentService
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    public async Task<AttachmentInfo> UploadAsync(AttachmentUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var access = await OwnerAccessAsync(upload.OwnerType, upload.OwnerId, cancellationToken).ConfigureAwait(false);
        Require(access.CanWrite, DocumentErrors.Forbidden, 403);
        var now = clock.UtcNow;
        var settings = await DocumentSettings.LoadAsync(c, access.CompanyId, actor.UserId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(upload.FileName) || upload.FileName.Length > 255 || upload.FileName.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new DocumentException(DocumentErrors.Invalid);
        }

        if (upload.Content.Length == 0)
        {
            throw new DocumentException(DocumentErrors.Empty);
        }

        if (upload.Content.Length > settings.MaxBytes)
        {
            throw new DocumentException(DocumentErrors.TooLarge, 413);
        }

        var type = FileValidation.Detect(upload.Content, upload.FileName);
        if (type is null || !settings.AllowedTypes.Contains(type))
        {
            throw new DocumentException(DocumentErrors.TypeNotAllowed, 415);
        }

        FileRow? previous = null;
        if (upload.ReplacesFileId is { } replaces)
        {
            previous = await FileRows.FindAsync(c, null, replaces, cancellationToken).ConfigureAwait(false);
            if (previous is null || previous.OwnerType != upload.OwnerType || previous.OwnerId != upload.OwnerId || previous.Status == FileStatuses.Deleted)
            {
                throw new DocumentException(DocumentErrors.NotFound, 404);
            }

            if (await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from doc.files n where n.previous_file_id = $1 and n.status <> 'deleted')", cancellationToken, previous.Id).ConfigureAwait(false))
            {
                throw new DocumentException(DocumentErrors.Invalid); // only the newest version can be replaced
            }
        }

        await EnsureRoomAsync(c, upload.Content.Length, cancellationToken).ConfigureAwait(false);
        var id = Guid.CreateVersion7(now);
        var key = $"{TenantFileKeys.Prefix(tenantContext.TenantCode)}/{id:N}";
        await store.PutAsync(key, await encryption.EncryptAsync(id, upload.Content, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var status = settings.ScanRequired ? FileStatuses.Uploading : FileStatuses.Clean;
        try
        {
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into doc.files (id, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, storage_key, version_no, previous_file_id, document_type, expires_on,
                    is_sensitive, status, legal_hold, key_version, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, false, $16, $17, $18, $17, $18, 1)
                """, cancellationToken, id, access.CompanyId, upload.OwnerType, upload.OwnerId, upload.FileName.Trim(), FileValidation.ContentTypeOf(type, upload.FileName), (long)upload.Content.Length,
                FileValidation.Checksum(upload.Content), key, (previous?.VersionNo ?? 0) + 1, previous?.Id, upload.DocumentType ?? previous?.DocumentType, upload.ExpiresOn ?? previous?.ExpiresOn,
                upload.IsSensitive || previous?.IsSensitive == true, status, TenantFileKeys.KeyVersion, now, actor.UserId).ConfigureAwait(false);
            var row = (await FileRows.FindAsync(c, tx, id, cancellationToken).ConfigureAwait(false))!;
            await Log(c, tx, row, "create", [("file_name", null, row.FileName), ("version_no", null, row.VersionNo), ("owner_type", null, row.OwnerType), ("owner_id", null, row.OwnerId), ("size_bytes", null, row.SizeBytes), ("status", null, row.Status)], now, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (settings.ScanRequired)
        {
            await scanning.ScanAsync(c, id, actor.UserId, cancellationToken).ConfigureAwait(false);
        }

        return (await FileRows.FindAsync(c, null, id, cancellationToken).ConfigureAwait(false))!.ToInfo();
    }

    public async Task<IReadOnlyList<AttachmentInfo>> ListAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        var access = await OwnerAccessAsync(ownerType, ownerId, cancellationToken).ConfigureAwait(false);
        Require(access.CanRead, DocumentErrors.NotFound, 404);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Notifications.NotificationConsumer.ReadAsync(c, null, $"""
            select {FileRows.Columns} from doc.files f
            where f.owner_type = $1 and f.owner_id = $2 and f.status <> 'deleted' and f.archived_at is null and {FileRows.IsHead} and ($3 or not f.is_sensitive)
            order by f.created_at desc, f.id
            """, FileRows.Read, cancellationToken, ownerType, ownerId, access.CanReadSensitive).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToInfo())];
    }

    public async Task<AttachmentInfo?> GetAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var file = await ReadableAsync(c, fileId, cancellationToken).ConfigureAwait(false);
        return file?.ToInfo();
    }

    public async Task<IReadOnlyList<AttachmentInfo>> VersionsAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await ReadableAsync(c, fileId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(DocumentErrors.NotFound, 404);
        var rows = await Notifications.NotificationConsumer.ReadAsync(c, null, $"""
            with recursive older as (
                select id, previous_file_id from doc.files where id = $1
                union all select p.id, p.previous_file_id from doc.files p join older o on o.previous_file_id = p.id),
            newer as (
                select id from doc.files where id = $1
                union all select n.id from doc.files n join newer w on n.previous_file_id = w.id)
            select {FileRows.Columns} from doc.files f where f.id in (select id from older union select id from newer) and f.status <> 'deleted' order by f.version_no desc
            """, FileRows.Read, cancellationToken, fileId).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToInfo())];
    }

    public async Task<AttachmentContent> DownloadAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var file = await ReadableAsync(c, fileId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(DocumentErrors.NotFound, 404);
        if (file.Status != FileStatuses.Clean || file.StorageKey == FileStatuses.PurgedKey)
        {
            throw new DocumentException(DocumentErrors.NotReady, 409);
        }

        var stored = await store.GetAsync(file.StorageKey, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(DocumentErrors.NotFound, 404);
        var content = await encryption.DecryptAsync(file.Id, stored, cancellationToken).ConfigureAwait(false);
        if (FileValidation.Checksum(content) != file.Checksum)
        {
            throw new InvalidOperationException($"The stored content of file {file.Id} does not match its checksum.");
        }

        if (file.IsSensitive)
        {
            await AuditDb.AppendAccessAsync(c, actor.UserId, "doc.files", file.Id, ["content"], "view", 1, audit.CorrelationId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        return new AttachmentContent(file.FileName, file.ContentType, content);
    }

    public async Task<AttachmentInfo> RestoreAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var source = await FileRows.FindAsync(c, null, fileId, cancellationToken).ConfigureAwait(false);
        var access = source is null ? null : await OwnerAccessAsync(source.OwnerType, source.OwnerId, cancellationToken, throwUnknown: false).ConfigureAwait(false);
        if (source is null || access is null || !access.CanRead || source.Status == FileStatuses.Deleted)
        {
            throw new DocumentException(DocumentErrors.NotFound, 404);
        }

        Require(access.CanWrite, DocumentErrors.Forbidden, 403);
        if (source.Status != FileStatuses.Clean || source.StorageKey == FileStatuses.PurgedKey)
        {
            throw new DocumentException(DocumentErrors.NotReady, 409);
        }

        var head = (await Notifications.NotificationConsumer.ReadAsync(c, null, $"""
            select {FileRows.Columns} from doc.files f where f.id in (select unnest($1::uuid[])) and f.status <> 'deleted' and {FileRows.IsHead}
            """, FileRows.Read, cancellationToken, (await FileRows.ChainAsync(c, null, fileId, cancellationToken).ConfigureAwait(false)).ToArray()).ConfigureAwait(false)).Single();
        if (head.Id == source.Id)
        {
            throw new DocumentException(DocumentErrors.Invalid); // already the current version
        }

        var content = await encryption.DecryptAsync(source.Id, await store.GetAsync(source.StorageKey, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(DocumentErrors.NotFound, 404), cancellationToken).ConfigureAwait(false);
        await EnsureRoomAsync(c, content.Length, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        var key = $"{TenantFileKeys.Prefix(tenantContext.TenantCode)}/{id:N}";
        await store.PutAsync(key, await encryption.EncryptAsync(id, content, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into doc.files (id, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, storage_key, version_no, previous_file_id, document_type, expires_on,
                    is_sensitive, status, legal_hold, key_version, scanned_at, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, 'clean', false, $15, $16, $17, $18, $17, $18, 1)
                """, cancellationToken, id, source.CompanyId, source.OwnerType, source.OwnerId, source.FileName, source.ContentType, source.SizeBytes, source.Checksum, key, head.VersionNo + 1, head.Id,
                source.DocumentType, source.ExpiresOn, source.IsSensitive || head.IsSensitive, TenantFileKeys.KeyVersion, source.ScannedAt, now, actor.UserId).ConfigureAwait(false);
            var row = (await FileRows.FindAsync(c, tx, id, cancellationToken).ConfigureAwait(false))!;
            await Log(c, tx, row, "create", [("file_name", null, row.FileName), ("version_no", null, row.VersionNo), ("restored_from", null, source.Id), ("status", null, row.Status)], now, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return (await FileRows.FindAsync(c, null, id, cancellationToken).ConfigureAwait(false))!.ToInfo();
    }

    public async Task DeleteAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var file = await ReadableAsync(c, fileId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(DocumentErrors.NotFound, 404);
        var access = await OwnerAccessAsync(file.OwnerType, file.OwnerId, cancellationToken).ConfigureAwait(false);
        Require(access.CanWrite, DocumentErrors.Forbidden, 403);
        var chain = (await FileRows.ChainAsync(c, null, fileId, cancellationToken).ConfigureAwait(false)).ToArray();
        if (await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from doc.files where id = any($1) and legal_hold)", cancellationToken, chain).ConfigureAwait(false))
        {
            throw new DocumentException(DocumentErrors.Held, 409);
        }

        var now = clock.UtcNow;
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ids = await Notifications.NotificationConsumer.ReadAsync(c, tx, """
            update doc.files set status = 'deleted', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = any($1) and status <> 'deleted' returning id
            """, r => r.GetGuid(0), cancellationToken, chain, now, actor.UserId).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var row = (await FileRows.FindAsync(c, tx, id, cancellationToken).ConfigureAwait(false))!;
            await Log(c, tx, row, "delete", [("status", file.Status, FileStatuses.Deleted)], now, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetLegalHoldAsync(Guid fileId, bool hold, string? reason, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(DocumentPermissions.LegalHold, cancellationToken).ConfigureAwait(false))
        {
            throw new DocumentException(DocumentErrors.Forbidden, 403);
        }

        if (hold && string.IsNullOrWhiteSpace(reason))
        {
            throw new DocumentException(DocumentErrors.Invalid);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // The right to hold is its own sensitive permission (legal and compliance staff need not read the record); no content is shown.
        _ = await FileRows.FindAsync(c, null, fileId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(DocumentErrors.NotFound, 404);
        var now = clock.UtcNow;
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ids = await Notifications.NotificationConsumer.ReadAsync(c, tx, """
            update doc.files set legal_hold = $2, legal_hold_reason = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1
            where id = any($1) and (legal_hold <> $2 or legal_hold_reason is distinct from $3) returning id
            """, r => r.GetGuid(0), cancellationToken, (await FileRows.ChainAsync(c, tx, fileId, cancellationToken).ConfigureAwait(false)).ToArray(), hold, hold ? reason!.Trim() : null, now, actor.UserId).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var row = (await FileRows.FindAsync(c, tx, id, cancellationToken).ConfigureAwait(false))!;
            await Log(c, tx, row, "update", [("legal_hold", !hold, hold), ("legal_hold_reason", null, row.LegalHoldReason)], now, cancellationToken).ConfigureAwait(false);
            await AuditDb.AppendSecurityEventAsync(c, tx, new SecurityEventRecord(now, hold ? DocumentEvents.LegalHoldSet : DocumentEvents.LegalHoldReleased, actor.UserId.ToString(), id.ToString(), Severities.Info,
                new System.Text.Json.Nodes.JsonObject { ["file_id"] = id.ToString(), ["reason"] = row.LegalHoldReason }, actor.UserId), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetentionReviewItem>> RetentionReviewAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(DocumentPermissions.RetentionReview, cancellationToken).ConfigureAwait(false))
        {
            throw new DocumentException(DocumentErrors.Forbidden, 403);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await Retention.DueAsync(c, "review", clock.UtcNow, cancellationToken).ConfigureAwait(false) is { } due
            ? [.. due.Select(d => new RetentionReviewItem(d.File.Id, d.File.OwnerType, d.File.OwnerId, d.File.FileName, d.File.DocumentType!, d.DueOn))]
            : [];
    }

    /// <summary>The file when it exists, is not deleted and its record may be read by the caller (sensitive files need the sensitive right); otherwise null.</summary>
    private async Task<FileRow?> ReadableAsync(NpgsqlConnection c, Guid fileId, CancellationToken ct)
    {
        var file = await FileRows.FindAsync(c, null, fileId, ct).ConfigureAwait(false);
        if (file is null || file.Status == FileStatuses.Deleted)
        {
            return null;
        }

        var access = await OwnerAccessAsync(file.OwnerType, file.OwnerId, ct, throwUnknown: false).ConfigureAwait(false);
        return access is { CanRead: true } && (!file.IsSensitive || access.CanReadSensitive) ? file : null;
    }

    private async Task<AttachmentOwnerAccess> OwnerAccessAsync(string ownerType, Guid ownerId, CancellationToken ct, bool throwUnknown = true)
    {
        var owner = owners.FirstOrDefault(o => string.Equals(o.OwnerType, ownerType, StringComparison.Ordinal));
        if (owner is null)
        {
            return throwUnknown ? throw new DocumentException(DocumentErrors.UnknownOwner) : new AttachmentOwnerAccess(Guid.Empty, false, false, false);
        }

        return await owner.AccessAsync(ownerId, ct).ConfigureAwait(false) is { CanRead: true } found ? found : throw new DocumentException(DocumentErrors.NotFound, 404);
    }

    private async Task EnsureRoomAsync(NpgsqlConnection c, long adding, CancellationToken ct)
    {
        var limit = licence.Licence.Claims?.Limit(LimitKeys.StorageGb);
        if (limit is not { } gb)
        {
            return;
        }

        var used = Convert.ToInt64(await IdentityDb.ScalarAsync<object>(c, null, LimitSql.StorageBytes, ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (used + adding > gb * BytesPerGb)
        {
            throw new DocumentException(DocumentErrors.StorageFull, 409);
        }
    }

    private Task Log(NpgsqlConnection c, NpgsqlTransaction tx, FileRow file, string action, IReadOnlyList<(string Field, object? Before, object? After)> changes, DateTimeOffset now, CancellationToken ct) =>
        FileRows.LogAsync(c, tx, file, action, changes, actor.UserId, audit.ActorKind, audit.Source, audit.CorrelationId, audit.IpAddress, now, ct);

    private static void Require(bool allowed, string error, int status)
    {
        if (!allowed)
        {
            throw new DocumentException(error, status);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
}
