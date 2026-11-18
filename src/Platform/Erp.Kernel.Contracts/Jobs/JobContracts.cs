using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Jobs;

/// <summary>
/// The code of one background job a module declares in its job manifest (slice S1.11 Build item 1, FR-KRN-JOB-01). The
/// worker host finds the handler by the job code and calls it inside a service scope bound to the customer, acting as
/// the job actor (scheduled runs) or the person who asked (requested runs), slice S1.11 ruling Q4.
/// </summary>
public interface IJobHandler
{
    /// <summary>The job code of the manifest, for example <c>kernel.licence_refresh</c>.</summary>
    string Code { get; }

    /// <summary>
    /// Does the run's work. Long work is done in chunks through <see cref="IJobExecution.CommitChunkAsync"/>, so a run
    /// that restarts on another worker continues after its last checkpoint (FR-KRN-JOB-02). Throwing fails the attempt;
    /// the runner retries it with back-off while the job's retry policy allows.
    /// </summary>
    Task RunAsync(IJobExecution job, CancellationToken cancellationToken);
}

/// <summary>What a running job sees of its run, and how it saves its progress.</summary>
public interface IJobExecution
{
    Guid RunId { get; }

    string Code { get; }

    /// <summary>The company the run is for, or null for the whole customer.</summary>
    Guid? CompanyId { get; }

    /// <summary>The run's parameters (an empty object when none).</summary>
    JsonElement Parameters { get; }

    /// <summary>The checkpoint saved by the last committed chunk, or null on a fresh start.</summary>
    JsonElement? Checkpoint { get; }

    /// <summary>1 for the first attempt, 2 for the first retry, ...</summary>
    int Attempt { get; }

    /// <summary>The schedule slot this run is for, or null for a run a person asked for.</summary>
    DateTimeOffset? ScheduledFor { get; }

    /// <summary>The run's service scope: bound to the customer, acting as the job actor or the requester (ruling Q4).</summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Commits one chunk: <paramref name="work"/> runs on the customer database in the same transaction that saves the new
    /// checkpoint and progress, after checking that this worker still holds the run and nobody asked to cancel it. So a
    /// chunk's work is saved exactly once. Throws <see cref="JobStoppedException"/> (and saves nothing) when the lease was
    /// lost or cancel was asked; the handler then returns at once.
    /// </summary>
    Task CommitChunkAsync(Func<DbConnection, DbTransaction, CancellationToken, Task>? work, object? checkpoint, decimal? progressPct, CancellationToken cancellationToken);

    /// <summary>Appends a line to the run's log (job.run_logs). Level: info, warning or error.</summary>
    Task LogAsync(string level, string message, CancellationToken cancellationToken);
}

/// <summary>The run stopped at a chunk boundary: cancel was asked, or another worker took the run over.</summary>
public sealed class JobStoppedException(string reason) : Exception(reason)
{
    public const string Cancelled = "cancelled";
    public const string LeaseLost = "lease_lost";
}

/// <summary>A job's own reason for failing, as a localization key (<c>errors.job.*</c>) shown in the job centre.</summary>
public sealed class JobFailedException(string errorKey) : Exception(errorKey);

/// <summary>A module's job manifest (JSON), registered by the module like its workflow manifest (S1.11 Build item 1).</summary>
public sealed record JobManifestSource(string Json);

/// <summary>An event read from a schema's outbox (spec 03 section 7.5) as the dispatcher hands it to a consumer.</summary>
public sealed record OutboxEvent(Guid Id, string Schema, string EventType, string AggregateType, Guid AggregateId, DateTimeOffset OccurredAt, JsonElement Payload, JsonElement Headers);

/// <summary>
/// A consumer of outbox events (slice S1.11 Build item 4, ruling Q7). The dispatcher calls it inside the transaction that
/// records the delivery in the schema's inbox, so work done on <c>connection</c> and <c>transaction</c> happens exactly
/// once per event and consumer. Work outside the customer database (an e-mail) must be made idempotent by the consumer.
/// Throwing rolls back the consumer's work; the event is retried with growing waits and never dropped.
/// </summary>
public interface IEventConsumer
{
    /// <summary>Stable name recorded in the inbox (for example <c>ntf.notifications</c>).</summary>
    string Name { get; }

    bool Accepts(string eventType);

    Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>Events of the jobs framework, written to <c>job.outbox_messages</c>.</summary>
public static class JobEvents
{
    /// <summary>A run failed after its last attempt (FR-KRN-JOB-06).</summary>
    public const string RunFailed = "kernel.job.run_failed";

    /// <summary>An operational alert (ruling Q8): payload kind, key, severity, details, owner_user_id.</summary>
    public const string AlertRaised = "kernel.alert.raised";

    /// <summary>A person queued a run, asked for a rerun, cancelled a run or changed a schedule (job centre).</summary>
    public const string RunRequested = "kernel.job.run_requested";

    public const string RunCancelRequested = "kernel.job.cancel_requested";

    public const string ScheduleChanged = "kernel.job.schedule_changed";
}

/// <summary>The kinds of operational alert (rulings Q8, Q9; R-012, R-014).</summary>
public static class AlertKinds
{
    public const string JobFailed = "job_failed";
    public const string JobStuck = "job_stuck";
    public const string WorkerStopped = "worker_stopped";
    public const string JobOverdue = "job_overdue";
    public const string EventBacklog = "event_backlog";
    public const string EventConsumerFailing = "event_consumer_failing";
    public const string AuditCritical = "audit_critical";
    public const string AuditVerifyFailed = "audit_verify_failed";
    public const string ClockAhead = "clock_ahead";
    public const string UnusualAccess = "unusual_access";
    public const string PartitionProblem = "partition_problem";
    public const string DocumentInfected = "document_infected";
}

/// <summary>
/// Permissions of the job centre (slice S1.11 Build item 7, FR-ADM-OPS-01). The generated job writes are closed
/// (CR-046 item 2, P2); these codes are kept as hand-written permissions of the job centre service.
/// </summary>
public static class JobPermissions
{
    public const string DefinitionsRead = "job.definitions.read";
    public const string DefinitionsUpdate = "job.definitions.update";
    public const string RunsRead = "job.runs.read";
    public const string RunsCreate = "job.runs.create";
    public const string RunsUpdate = "job.runs.update";
    public const string LogsRead = "job.run_logs.read";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(DefinitionsUpdate, "job", "permissions.job.definitions.update", false),
        new(RunsCreate, "job", "permissions.job.runs.create", false),
        new(RunsUpdate, "job", "permissions.job.runs.update", false),
    ];
}
