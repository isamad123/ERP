using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>Settings of the kernel jobs that act on the control plane (slice S1.11 ruling P6).</summary>
/// <param name="PlatformActorEmail">The platform user the worker acts as when it copies audit anchors (<c>ERP_PLATFORM_ACTOR</c>).</param>
public sealed record KernelJobOptions(string? PlatformActorEmail);

/// <summary>
/// The kernel's jobs (slice S1.11 Build item 5): the hand-written loops and operator commands of earlier slices, now run
/// by the jobs framework. Each does for the run's customer what the loop or command did; the operator commands stay.
/// </summary>
public static class KernelJobs
{
    public const string LicenceRefresh = "kernel.licence_refresh";
    public const string WorkflowEscalate = "kernel.workflow_escalate";
    public const string AuditAnchorsPublish = "kernel.audit_anchors_publish";
    public const string AuditVerify = "kernel.audit_verify";
    public const string PartitionsEnsure = "kernel.partitions_ensure";
    public const string MakerCheckerExpire = "kernel.maker_checker_expire";
    public const string OpsAlertsCheck = "kernel.ops_alerts";
    public const string Purge = "kernel.purge";

    /// <summary>Months of partitions kept ahead: this month and the next three (slice S1.11 Build item 5).</summary>
    public const int PartitionMonths = 4;

    /// <summary>Delivered events and inbox rows are removed after this many days (spec 03 section 8).</summary>
    public const int EventRetentionDays = 30;

    /// <summary>Chain entries this far ahead of the database clock raise the clock-ahead alert (R-014, CR-032).</summary>
    public static readonly TimeSpan ClockAheadAllowed = TimeSpan.FromMinutes(5);

    /// <summary>The kernel's handlers, registered as singletons; each resolves what it needs from the run's scope.</summary>
    public static IServiceCollection AddKernelJobHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IJobHandler, LicenceRefreshJob>();
        services.AddSingleton<IJobHandler, WorkflowEscalateJob>();
        services.AddSingleton<IJobHandler, AuditAnchorsPublishJob>();
        services.AddSingleton<IJobHandler, AuditVerifyJob>();
        services.AddSingleton<IJobHandler, PartitionsEnsureJob>();
        services.AddSingleton<IJobHandler, MakerCheckerExpireJob>();
        services.AddSingleton<IJobHandler, OpsAlertsJob>();
        services.AddSingleton<IJobHandler, PurgeJob>();

        // E-mail (slice S1.11b2): the sender and the bounce reader; their mail settings come with the notification consumer.
        services.AddSingleton<IJobHandler, Notifications.Email.EmailSendJob>();
        services.AddSingleton<IJobHandler, Notifications.Email.EmailBounceJob>();

        // Files (slice S1.11c1): scanning, retention and expiry reminders; the hosts that run jobs also call AddKernelDocuments.
        services.AddSingleton<IJobHandler, Documents.DocumentScanJob>();
        services.AddSingleton<IJobHandler, Documents.DocumentRetentionJob>();
        services.AddSingleton<IJobHandler, Documents.DocumentExpiryJob>();
        services.AddSingleton<IJobHandler, Documents.Templates.GenerateBatchJob>();
        services.AddSingleton<IJobHandler, Itg.ImportValidateJob>();
        services.AddSingleton<IJobHandler, Itg.ImportCommitJob>();
        return services;
    }

    internal static async Task<NpgsqlConnection> OpenAsync(IJobExecution job, CancellationToken ct) =>
        (NpgsqlConnection)await job.Services.GetRequiredService<ITenantConnectionProvider>().DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    internal static string TenantCode(IJobExecution job) => job.Services.GetRequiredService<ITenantContext>().TenantCode;
}

/// <summary>Hourly licence refresh of the run's customer (was <c>LicenceLoop</c>, S1.09b ruling Q6).</summary>
internal sealed class LicenceRefreshJob(LicenceMaintenance maintenance) : IJobHandler
{
    public string Code => KernelJobs.LicenceRefresh;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        var outcome = Assert(await maintenance.RunAsync(KernelJobs.TenantCode(job), cancellationToken).ConfigureAwait(false));
        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture,
            $"state {outcome.State}{(outcome.Changed ? $" (was {outcome.PreviousState})" : string.Empty)}; {outcome.Readings} usage reading(s); {outcome.LimitEventsCopied} limit event(s) copied{(outcome.Activated is { } v ? $"; entitlement version {v} in force" : string.Empty)}"), cancellationToken).ConfigureAwait(false);
    }

    private static LicenceRefreshOutcome Assert(IReadOnlyList<LicenceRefreshOutcome> outcomes)
    {
        var outcome = outcomes.Count == 1 ? outcomes[0] : throw new InvalidOperationException("the customer was not found for the licence refresh");
        return outcome.Error is null ? outcome : throw new InvalidOperationException(outcome.Error);
    }
}

/// <summary>Approval escalation of the run's customer every minute (was <c>WorkflowEscalationLoop</c>, S1.10b ruling Q9; FR-XM-32).</summary>
internal sealed class WorkflowEscalateJob : IJobHandler
{
    public string Code => KernelJobs.WorkflowEscalate;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        var run = await job.Services.GetRequiredService<WorkflowEngine>().EscalateAsync(cancellationToken).ConfigureAwait(false);
        if (run.Escalated > 0 || run.NotPossible > 0 || run.DueDatesSet > 0)
        {
            await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{run.Escalated} escalated, {run.NotPossible} with nobody above, {run.DueDatesSet} time limits set"), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Daily copy of finished days' audit anchors to the control plane (was the operator command, S1.07 P5; ruling P6).</summary>
internal sealed class AuditAnchorsPublishJob(ControlPlaneOptions controlPlane, ISecretStore secrets, IClock clock, KernelJobOptions options) : IJobHandler
{
    public const string NoPlatformActor = "errors.job.platform_actor_missing";

    public string Code => KernelJobs.AuditAnchorsPublish;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PlatformActorEmail))
        {
            await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
            await OpsAlerts.RaiseAsync(c, null, AlertKinds.JobFailed, $"{AlertKinds.JobFailed}:{Code}:platform_actor", OpsAlerts.Error, new { code = Code, error = NoPlatformActor }, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            throw new JobFailedException(NoPlatformActor);
        }

        var actor = await new ControlPlaneAdmin(controlPlane, secrets, clock).ActorAsync(options.PlatformActorEmail, cancellationToken).ConfigureAwait(false);
        var migrations = new MigrationSet(new Dictionary<string, IReadOnlyList<string>>(), []);
        var result = (await new AnchorPublisher(controlPlane, secrets, clock, migrations).PublishAsync(actor, KernelJobs.TenantCode(job), cancellationToken).ConfigureAwait(false)).Single();
        await job.LogAsync(result.Mismatches.Count == 0 ? "info" : "error", result.Mismatches.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{result.Copied} anchor(s) copied, {result.AlreadyCopied} already copied and unchanged")
            : "anchor(s) changed since they were copied: " + string.Join(", ", result.Mismatches), cancellationToken).ConfigureAwait(false);
        if (result.Mismatches.Count > 0)
        {
            // The mismatch is a critical security event (aud.anchor_copy_mismatch); the alert job reports it at once too.
            await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
            await OpsAlerts.RaiseAsync(c, null, AlertKinds.AuditCritical, $"{AlertKinds.AuditCritical}:anchors:{string.Join(",", result.Mismatches)}", OpsAlerts.Critical, new { event_type = AnchorPublisher.MismatchEvent, days = result.Mismatches }, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            throw new JobFailedException("errors.job.audit_anchor_mismatch");
        }
    }
}

/// <summary>
/// Nightly chain check of the previous day (R-012) and the check that the chain head is not ahead of the database clock
/// (R-014). A broken chain raises a critical alert and fails the run.
/// </summary>
internal sealed class AuditVerifyJob(ControlPlaneOptions controlPlane, IClock clock) : IJobHandler
{
    public string Code => KernelJobs.AuditVerify;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        var day = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-1);
        var copies = await AuditAnchorCopies.ReadAsync(controlPlane, KernelJobs.TenantCode(job), day, day, cancellationToken).ConfigureAwait(false);
        await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
        var result = await ChainVerifier.VerifyAsync(c, copies, day, day, markVerified: true, clock, cancellationToken).ConfigureAwait(false);
        await job.LogAsync(result.Intact ? "info" : "error", result.Intact
            ? string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd}: {result.RowsChecked} row(s) checked, intact; control-plane copies compared: {(result.ControlPlaneCompared ? "yes" : "no")}")
            : string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd}: BROKEN at row {result.BrokenRowId}: {result.Problem}"), cancellationToken).ConfigureAwait(false);

        // R-014: the chain never goes back in time, so a head ahead of the database clock means a server clock ran ahead.
        var ahead = await IdentityDb.ScalarAsync<TimeSpan?>(c, null, "select max(occurred_at) - now() from aud.change_log", cancellationToken).ConfigureAwait(false);
        if (ahead is { } by && by > KernelJobs.ClockAheadAllowed)
        {
            await job.LogAsync("warning", string.Create(CultureInfo.InvariantCulture, $"the audit chain head is {by} ahead of the database clock"), cancellationToken).ConfigureAwait(false);
            await OpsAlerts.RaiseAsync(c, null, AlertKinds.ClockAhead, $"{AlertKinds.ClockAhead}:head:{DateOnly.FromDateTime(clock.UtcNow.UtcDateTime):yyyy-MM-dd}", OpsAlerts.Warning, new { ahead_seconds = (long)by.TotalSeconds }, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Intact)
        {
            await OpsAlerts.RaiseAsync(c, null, AlertKinds.AuditVerifyFailed, $"{AlertKinds.AuditVerifyFailed}:{day:yyyy-MM-dd}", OpsAlerts.Critical, new { day, broken_row_id = result.BrokenRowId, problem = result.Problem }, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            throw new JobFailedException("errors.job.audit_chain_broken");
        }
    }
}

/// <summary>Monthly partitions three months ahead (spec 03 section 8, FR-HRM-DAT-04), through <c>meta.ensure_partitions</c>.</summary>
internal sealed class PartitionsEnsureJob(IClock clock) : IJobHandler
{
    public string Code => KernelJobs.PartitionsEnsure;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
        var outcomes = await OpsAlerts.ReadAsync(c, "select table_name, partition_name, outcome from meta.ensure_partitions($1, $2)",
            r => (Table: r.GetString(0), Partition: r.GetString(1), Outcome: r.GetString(2)), cancellationToken, new DateOnly(today.Year, today.Month, 1), KernelJobs.PartitionMonths).ConfigureAwait(false);
        foreach (var o in outcomes)
        {
            await job.LogAsync(o.Outcome == "created" ? "info" : "error", $"{o.Table}: {o.Partition} {o.Outcome}", cancellationToken).ConfigureAwait(false);
        }

        var problems = outcomes.Where(o => o.Outcome != "created").ToList();
        if (problems.Count > 0)
        {
            await OpsAlerts.RaiseAsync(c, null, AlertKinds.PartitionProblem, $"{AlertKinds.PartitionProblem}:{string.Join(",", problems.Select(p => p.Partition))}", OpsAlerts.Error,
                new { partitions = problems.Select(p => new { table = p.Table, partition = p.Partition, outcome = p.Outcome }) }, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            throw new JobFailedException("errors.job.partition_default_has_rows");
        }
    }
}

/// <summary>Daily expiry of maker-checker requests waiting longer than <c>iam.maker_checker.expiry_days</c> (S1.06 handoff).</summary>
internal sealed class MakerCheckerExpireJob : IJobHandler
{
    public string Code => KernelJobs.MakerCheckerExpire;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        var expired = await job.Services.GetRequiredService<AccessAdministrationService>().ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{expired} pending change(s) expired"), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Operational checks every five minutes (FR-KRN-JOB-06, rulings Q8, Q9). Keeps its audit high-water mark as its checkpoint.</summary>
internal sealed class OpsAlertsJob(IClock clock) : IJobHandler
{
    /// <summary>On the first run, critical audit events of the last hour are looked at.</summary>
    private static readonly TimeSpan FirstLook = TimeSpan.FromHours(1);

    public string Code => KernelJobs.OpsAlertsCheck;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
        var previous = await IdentityDb.ScalarAsync<string?>(c, null, """
            select r.checkpoint::text from job.runs r join job.definitions d on d.id = r.definition_id
            where d.code = $1 and r.status = 'succeeded' and r.checkpoint is not null order by r.finished_at desc limit 1
            """, cancellationToken, Code).ConfigureAwait(false);
        var since = now - FirstLook;
        if (previous is not null)
        {
            using var doc = JsonDocument.Parse(previous);
            if (doc.RootElement.TryGetProperty("audit_seen_until", out var seen) && seen.TryGetDateTimeOffset(out var at))
            {
                since = at;
            }
        }

        var result = await OpsAlerts.CheckAsync(c, now, since, cancellationToken).ConfigureAwait(false);
        await job.CommitChunkAsync(null, new { audit_seen_until = result.AuditSeenUntil }, 100, cancellationToken).ConfigureAwait(false);
        if (result.Raised.Count > 0)
        {
            await job.LogAsync("warning", "alerts raised: " + string.Join(", ", result.Raised), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Daily clean-up (ruling P3; spec 03 section 8, FR-HRM-DAT-04): finished runs after <c>job.retention.run_days</c>
/// (default 30; a run an import batch points to is kept), delivered outbox rows, inbox rows and finished e-mails of the
/// delivery log (S1.11b2) after 30 days. Run logs
/// are append-only and go with their monthly partition (later slice).
/// </summary>
internal sealed class PurgeJob(IClock clock) : IJobHandler
{
    public string Code => KernelJobs.Purge;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var c = await KernelJobs.OpenAsync(job, cancellationToken).ConfigureAwait(false);
        var settings = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.JobRetentionRunDays], null, null, today, cancellationToken).ConfigureAwait(false);
        var days = settings.TryGetValue(KernelSettingKeys.JobRetentionRunDays, out var s) && s.Value.ValueKind == JsonValueKind.Number && s.Value.TryGetInt32(out var n) && n >= 7 ? n : 30;
        var runs = await IdentityDb.ExecuteAsync(c, null, """
            delete from job.runs r where r.status in ('succeeded', 'failed', 'cancelled') and r.finished_at < $1
              and not exists (select 1 from itg.import_batches b where b.job_run_id = r.id)
            """, cancellationToken, now.AddDays(-days)).ConfigureAwait(false);
        var kept = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.NtfInAppRetentionDays], null, null, today, cancellationToken).ConfigureAwait(false);
        var itemDays = kept.TryGetValue(KernelSettingKeys.NtfInAppRetentionDays, out var k) && k.Value.ValueKind == JsonValueKind.Number && k.Value.TryGetInt32(out var m) && m >= 30 ? m : 180;
        var items = await IdentityDb.ExecuteAsync(c, null, "delete from ntf.in_app_items where created_at < $1", cancellationToken, now.AddDays(-itemDays)).ConfigureAwait(false);

        // Form drafts (S1.21a, CR-077): a draft is kept for cfg.drafts.keep_days after its last save; saving renews expires_at, so only abandoned drafts go.
        var drafts = await IdentityDb.ExecuteAsync(c, null, "delete from cfg.form_drafts where expires_at < $1", cancellationToken, now).ConfigureAwait(false);

        // The e-mail delivery log keeps finished messages 30 days (spec 03 section 8; slice S1.11b2); waiting ones stay.
        var mails = await IdentityDb.ExecuteAsync(c, null, "delete from ntf.outbound_messages where status <> 'queued' and updated_at < $1", cancellationToken,
            now.AddDays(-KernelJobs.EventRetentionDays)).ConfigureAwait(false);
        var events = 0;
        var inbox = 0;
        foreach (var schema in await OpsAlerts.OutboxSchemasAsync(c, cancellationToken, "DELETE").ConfigureAwait(false))
        {
#pragma warning disable CA2100 // the schema name comes from the catalog and is quoted; values are parameters
            events += await IdentityDb.ExecuteAsync(c, null, $"delete from {OpsAlerts.Quote(schema)}.outbox_messages where published_at < $1", cancellationToken, now.AddDays(-KernelJobs.EventRetentionDays)).ConfigureAwait(false);
            inbox += await IdentityDb.ExecuteAsync(c, null, $"delete from {OpsAlerts.Quote(schema)}.inbox_messages where processed_at < $1", cancellationToken, now.AddDays(-KernelJobs.EventRetentionDays)).ConfigureAwait(false);
#pragma warning restore CA2100
        }

        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{runs} finished run(s) older than {days} days, {items} notification item(s) older than {itemDays} days, {drafts} unfinished form draft(s), {mails} finished e-mail(s), {events} delivered event(s) and {inbox} inbox row(s) older than {KernelJobs.EventRetentionDays} days removed"), cancellationToken).ConfigureAwait(false);
    }
}
