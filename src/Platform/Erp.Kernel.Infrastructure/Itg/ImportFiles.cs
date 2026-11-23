using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Infrastructure.Documents;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>An upload or a read the import centre refuses, with the HTTP status of the answer (the error is a localization key, <c>errors.import.*</c>).</summary>
public sealed class ImportRefusedException(string errorKey, int status = 400) : Exception(errorKey)
{
    public int Status { get; } = status;
}

/// <summary>
/// Keeps the uploaded import files (slice S1.16a, FR-KRN-INT-04): encrypted with the customer's file key in the file store, a row
/// in <c>doc.files</c> owned by the batch, and the virus scan before anything reads the file. A file that is infected, or that
/// could not be scanned while scanning is required, is refused and never read.
/// </summary>
public sealed class ImportFiles(ITenantConnectionProvider tenant, ITenantContext tenantContext, IClock clock, ICurrentActor actor, IAuditContext audit,
    IFileStore store, FileEncryption encryption, FileScanning scanning)
{
    public const string OwnerType = "itg.import_batches";

    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Stores the file for the batch <paramref name="batchId"/> and returns its file id and format. Throws <see cref="ImportRefusedException"/>.</summary>
    public async Task<(Guid FileId, string Format)> StoreAsync(byte[] content, string fileName, Guid companyId, Guid batchId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 || fileName.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ImportRefusedException("errors.import.unsupported_file");
        }

        if (content.Length == 0)
        {
            throw new ImportRefusedException("errors.import.empty_file");
        }

        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var settings = await DocumentSettings.LoadAsync(c, companyId, actor.UserId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false);
        if (content.Length > settings.MaxBytes)
        {
            throw new ImportRefusedException("errors.import.too_large", 413);
        }

        var format = ImportFileReader.Detect(content, fileName) ?? throw new ImportRefusedException("errors.import.unsupported_file", 415);
        var id = Guid.CreateVersion7(now);
        var key = $"{TenantFileKeys.Prefix(tenantContext.TenantCode)}/{id:N}";
        await store.PutAsync(key, await encryption.EncryptAsync(id, content, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                await IdentityDb.ExecuteAsync(c, tx, """
                    insert into doc.files (id, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, storage_key, version_no, document_type,
                        is_sensitive, status, legal_hold, key_version, created_at, created_by, updated_at, updated_by, row_version)
                    values ($1, $2, $3, $4, $5, $6, $7, $8, $9, 1, 'import', true, $10, false, $11, $12, $13, $12, $13, 1)
                    """, cancellationToken, id, companyId, OwnerType, batchId, fileName.Trim(), format == ImportFileReader.Xlsx ? XlsxType : "text/csv", (long)content.Length,
                    FileValidation.Checksum(content), key, settings.ScanRequired ? FileStatuses.Uploading : FileStatuses.Clean, TenantFileKeys.KeyVersion, now, actor.UserId).ConfigureAwait(false);
                var row = (await FileRows.FindAsync(c, tx, id, cancellationToken).ConfigureAwait(false))!;
                await FileRows.LogAsync(c, tx, row, "create", [("file_name", null, row.FileName), ("owner_type", null, row.OwnerType), ("owner_id", null, row.OwnerId)],
                    actor.UserId, audit.ActorKind, audit.Source, audit.CorrelationId, audit.IpAddress, now, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (settings.ScanRequired)
            {
                var verdict = await scanning.ScanAsync(c, id, actor.UserId, cancellationToken).ConfigureAwait(false);
                if (verdict == ScanVerdict.Infected)
                {
                    throw new ImportRefusedException("errors.import.infected", 422);
                }

                if (verdict != ScanVerdict.Clean)
                {
                    await IdentityDb.ExecuteAsync(c, null, "update doc.files set status = 'deleted', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1", cancellationToken, id, now, actor.UserId).ConfigureAwait(false);
                    await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
                    throw new ImportRefusedException("errors.import.not_scanned", 503);
                }
            }
        }
        catch (ImportRefusedException)
        {
            throw;
        }
        catch
        {
            await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return (id, format);
    }

    /// <summary>The decrypted content of a stored import file; only a clean file is read.</summary>
    public async Task<byte[]> ReadAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var file = await FileRows.FindAsync(c, null, fileId, cancellationToken).ConfigureAwait(false) ?? throw new ImportRefusedException("errors.import.not_found", 404);
        if (file.Status == FileStatuses.Quarantined || file.Status == FileStatuses.Infected)
        {
            throw new ImportRefusedException("errors.import.infected", 422);
        }

        if (file.Status != FileStatuses.Clean || file.StorageKey == FileStatuses.PurgedKey)
        {
            throw new ImportRefusedException("errors.import.not_scanned", 409);
        }

        var stored = await store.GetAsync(file.StorageKey, cancellationToken).ConfigureAwait(false) ?? throw new ImportRefusedException("errors.import.not_found", 404);
        var content = await encryption.DecryptAsync(file.Id, stored, cancellationToken).ConfigureAwait(false);
        if (FileValidation.Checksum(content) != file.Checksum)
        {
            throw new InvalidOperationException($"The stored content of file {file.Id} does not match its checksum.");
        }

        return content;
    }
}
