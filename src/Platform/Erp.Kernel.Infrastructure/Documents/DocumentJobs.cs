using System.Globalization;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Scans files that wait for it (slice S1.11c1; KRN-DOC-02, ruling Q21). Runs every ten minutes, so a file that waited for a
/// scanner that was down is checked soon after the scanner is back.
/// </summary>
public sealed class DocumentScanJob : IJobHandler
{
    public const string JobCode = "kernel.doc_scan";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
        var outcome = await job.Services.GetRequiredService<FileScanning>().ScanPendingAsync(c, 200, cancellationToken).ConfigureAwait(false);
        if (outcome.Clean + outcome.Infected + outcome.Waiting > 0)
        {
            await job.LogAsync(outcome.Infected > 0 ? "warning" : "info", string.Create(CultureInfo.InvariantCulture, $"{outcome.Clean} clean, {outcome.Infected} infected, {outcome.Waiting} waiting (scanner not reached)"), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Applies the retention rules daily (KRN-DOC-04, ruling Q24): archive hides a file from normal lists, delete destroys the
/// content and keeps the row. A file under legal hold is never touched; types without a rule are never touched.
/// </summary>
public sealed class DocumentRetentionJob(IClock clock) : IJobHandler
{
    public const string JobCode = "kernel.doc_retention";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
        var (archived, deleted) = await ApplyAsync(c, job.Services.GetRequiredService<IFileStore>(), clock.UtcNow, job.RunId, cancellationToken).ConfigureAwait(false);
        if (archived + deleted > 0)
        {
            await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{archived} files archived, {deleted} files destroyed"), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Applies every rule as of <paramref name="now"/>; returns the files archived and the files whose content was destroyed.</summary>
    public static async Task<(int Archived, int Deleted)> ApplyAsync(NpgsqlConnection c, IFileStore store, DateTimeOffset now, Guid? runId, CancellationToken ct)
    {
        var actor = BaselineSeed.SystemUserId;
        var archived = 0;
        var deleted = 0;
        foreach (var due in await Retention.DueAsync(c, "archive", now, ct).ConfigureAwait(false))
        {
            await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, "update doc.files set archived_at = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1 and archived_at is null and not legal_hold", ct, due.File.Id, now, actor).ConfigureAwait(false);
            await FileRows.LogAsync(c, tx, due.File, "update", [("archived_at", null, now)], actor, ActorKinds.Job, AuditSources.Job, runId, null, now, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            archived++;
        }

        foreach (var due in await Retention.DueAsync(c, "delete", now, ct).ConfigureAwait(false))
        {
            await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var changed = await IdentityDb.ExecuteAsync(c, tx, "update doc.files set status = 'deleted', storage_key = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1 and not legal_hold and storage_key <> $2",
                ct, due.File.Id, FileStatuses.PurgedKey, now, actor).ConfigureAwait(false);
            if (changed == 1)
            {
                await FileRows.LogAsync(c, tx, due.File, "delete", [("status", due.File.Status, FileStatuses.Deleted), ("content", "stored", "destroyed")], actor, ActorKinds.Job, AuditSources.Job, runId, null, now, ct).ConfigureAwait(false);
                await store.DeleteAsync(due.File.StorageKey, ct).ConfigureAwait(false);
                deleted++;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return (archived, deleted);
    }
}

/// <summary>
/// Reminders for documents that expire (KRN-DOC-08, ruling Q25): at each of the days set (30, 7 and 0 by default) before the
/// expiry date, one notification event to the person who uploaded the file. A day the job did not run is caught up with the
/// next smaller threshold; each threshold is raised once per file.
/// </summary>
public sealed class DocumentExpiryJob(IClock clock) : IJobHandler
{
    public const string JobCode = "kernel.doc_expiry";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
        var raised = await RaiseAsync(c, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (raised > 0)
        {
            await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{raised} expiry reminders raised"), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<int> RaiseAsync(NpgsqlConnection c, DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var thresholds = (await DocumentSettings.LoadAsync(c, null, null, today, ct).ConfigureAwait(false)).ReminderDays;
        if (thresholds.Count == 0)
        {
            return 0;
        }

        var files = await Notifications.NotificationConsumer.ReadAsync(c, null, $"""
            select {FileRows.Columns} from doc.files f
            where f.expires_on is not null and f.expires_on >= $1 and f.expires_on <= $2 and f.status in ('clean', 'uploading') and f.archived_at is null and f.storage_key <> $3 and {FileRows.IsHead}
            """, FileRows.Read, ct, today, today.AddDays(thresholds[^1]), FileStatuses.PurgedKey).ConfigureAwait(false);
        var raised = 0;
        foreach (var file in files)
        {
            var left = file.ExpiresOn!.Value.DayNumber - today.DayNumber;
            var threshold = thresholds.First(t => t >= left);
            if (await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from job.outbox_messages where event_type = $1 and aggregate_id = $2 and payload ->> 'threshold' = $3)",
                ct, DocumentEvents.Expiring, file.Id, threshold.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false))
            {
                continue;
            }

            await JobStore.RaiseAsync(c, null, DocumentEvents.Expiring, "doc.file", file.Id, now, new
            {
                recipients = new[] { file.CreatedBy },
                link = $"/documents/{file.Id}",
                company_id = file.CompanyId,
                file_name = file.FileName,
                days = left,
                threshold,
            }, ct).ConfigureAwait(false);
            raised++;
        }

        return raised;
    }
}
