using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Jobs;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Scans files that wait for it (slice S1.11c1; KRN-DOC-02, ruling Q21). A clean file becomes <c>clean</c> and can be
/// downloaded; an infected one is <c>quarantined</c> with a security event and an alert; when the scanner cannot be reached
/// the file keeps waiting. Used right after an upload and by the daily scan job.
/// </summary>
public sealed class FileScanning(IFileStore store, FileEncryption encryption, IVirusScanner scanner, IClock clock)
{
    public sealed record Outcome(int Clean, int Infected, int Waiting);

    /// <summary>Scans the file when it still waits; returns the verdict, or null when it was not waiting.</summary>
    public async Task<ScanVerdict?> ScanAsync(NpgsqlConnection c, Guid fileId, Guid actor, CancellationToken ct)
    {
        var file = await FileRows.FindAsync(c, null, fileId, ct).ConfigureAwait(false);
        if (file is null || file.Status != FileStatuses.Uploading)
        {
            return null;
        }

        var stored = await store.GetAsync(file.StorageKey, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return ScanVerdict.Unavailable;
        }

        var (verdict, detail) = await scanner.ScanAsync(await encryption.DecryptAsync(file.Id, stored, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        if (verdict == ScanVerdict.Unavailable)
        {
            return verdict;
        }

        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var status = verdict == ScanVerdict.Clean ? FileStatuses.Clean : FileStatuses.Quarantined;
        var changed = await IdentityDb.ExecuteAsync(c, tx, "update doc.files set status = $2, scanned_at = $3, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1 and status = 'uploading'",
            ct, file.Id, status, now, actor).ConfigureAwait(false);
        if (changed == 1)
        {
            await FileRows.LogAsync(c, tx, file, "status", [("status", FileStatuses.Uploading, status)], actor, ActorKinds.System, AuditSources.System, null, null, now, ct).ConfigureAwait(false);
            if (verdict == ScanVerdict.Infected)
            {
                await AuditDb.AppendSecurityEventAsync(c, tx, new SecurityEventRecord(now, DocumentEvents.Infected, actor.ToString(), file.Id.ToString(), Severities.Critical,
                    new JsonObject { ["file_id"] = file.Id.ToString(), ["file_name"] = file.FileName, ["owner_type"] = file.OwnerType, ["owner_id"] = file.OwnerId.ToString(), ["virus"] = detail }, actor), ct).ConfigureAwait(false);
                await OpsAlerts.RaiseAsync(c, tx, AlertKinds.DocumentInfected, $"{AlertKinds.DocumentInfected}:{file.Id}", OpsAlerts.Critical,
                    new { file_id = file.Id, file_name = file.FileName, virus = detail }, file.CreatedBy, now, ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return verdict;
    }

    /// <summary>Scans every file that waits (oldest first, at most <paramref name="limit"/>).</summary>
    public async Task<Outcome> ScanPendingAsync(NpgsqlConnection c, int limit, CancellationToken ct)
    {
        var ids = await Notifications.NotificationConsumer.ReadAsync(c, null, "select id from doc.files where status = 'uploading' order by created_at, id limit $1", r => r.GetGuid(0), ct, limit).ConfigureAwait(false);
        int clean = 0, infected = 0, waiting = 0;
        foreach (var id in ids)
        {
            switch (await ScanAsync(c, id, BaselineSeed.SystemUserId, ct).ConfigureAwait(false))
            {
                case ScanVerdict.Clean:
                    clean++;
                    break;
                case ScanVerdict.Infected:
                    infected++;
                    break;
                default:
                    waiting++;
                    break;
            }
        }

        return new Outcome(clean, infected, waiting);
    }
}
