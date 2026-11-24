using System.Text.Json;

namespace Erp.Kernel.Application.Jobs;

/// <summary>A job of the customer as the job centre shows it (FR-ADM-OPS-01; slice S1.11 Build item 7).</summary>
public sealed record JobDefinitionView(
    Guid Id,
    string Code,
    string Module,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    JsonElement? Schedule,
    bool IsEnabled,
    int Priority,
    bool Manual,
    bool ScheduleEditable,
    bool Licensed,
    IReadOnlyList<string> RunsIn,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastStatus,
    long RowVersion);

/// <summary>One run (FR-KRN-JOB-03).</summary>
public sealed record JobRunView(
    Guid Id,
    Guid DefinitionId,
    string Code,
    string Status,
    Guid? CompanyId,
    decimal? ProgressPct,
    int Attempt,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? CancelRequestedAt,
    string? Error,
    Guid? RequestedBy,
    Guid? ActorUserId,
    JsonElement? Parameters,
    DateTimeOffset CreatedAt);

public sealed record JobLogView(DateTimeOffset At, string Level, string Message);

public sealed record JobRunDetail(JobRunView Run, IReadOnlyList<JobLogView> Logs);

/// <summary>Filters of the run list: status, job code, company, created between (UTC), paging (limit at most 200).</summary>
public sealed record JobRunQuery(string? Status, string? Code, Guid? CompanyId, DateTimeOffset? From, DateTimeOffset? To, int? Limit, int? Offset);

/// <summary>"Run now": the company the run is for (optional) and its parameters.</summary>
public sealed record RunJobRequest(Guid? CompanyId, Dictionary<string, JsonElement>? Parameters);

/// <summary>A schedule change (permitted jobs only): the new schedule (or null to keep it) and on/off.</summary>
public sealed record JobScheduleRequest(JsonElement? Schedule, bool IsEnabled, long RowVersion);

/// <summary>Events waiting in one schema's outbox.</summary>
public sealed record EventBacklogView(string Schema, int Waiting, int Failing, DateTimeOffset? OldestAt);

/// <summary>
/// Health numbers of the jobs and events (FR-ADM-OPS-05, the parts from this slice): queue depth, running runs, failed runs
/// in the last 24 hours, the oldest waiting run, the last time any worker started a run (ruling P8: the every-minute
/// escalation job keeps this fresh while a worker serves the customer) and the event backlog per schema.
/// </summary>
public sealed record JobHealthView(int Queued, int Running, int FailedLast24Hours, DateTimeOffset? OldestQueuedAt, DateTimeOffset? LastWorkerActivityAt, bool WorkerSeenRecently, IReadOnlyList<EventBacklogView> Events);

/// <summary>A refusal of the job centre; <see cref="Exception.Message"/> is a localization key (<c>errors.job.*</c>).</summary>
public sealed class JobCentreException : Exception
{
    public JobCentreException(string errorKey, int status = 400, IReadOnlyList<string>? details = null)
        : base(errorKey)
    {
        Status = status;
        Details = details ?? [];
    }

    public JobCentreException()
        : this("errors.job.invalid")
    {
    }

    public JobCentreException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = 400;
        Details = [];
    }

    public int Status { get; }

    public IReadOnlyList<string> Details { get; }
}

/// <summary>The job centre (FR-KRN-JOB-03, FR-ADM-OPS-01). Every method checks the caller's permissions and data scope.</summary>
public interface IJobCentre
{
    Task<IReadOnlyList<JobDefinitionView>> DefinitionsAsync(CancellationToken cancellationToken);

    Task<JobDefinitionView> ChangeScheduleAsync(Guid definitionId, JobScheduleRequest request, CancellationToken cancellationToken);

    Task<JobRunView> RunNowAsync(Guid definitionId, RunJobRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<JobRunView>> RunsAsync(JobRunQuery query, CancellationToken cancellationToken);

    Task<JobRunDetail?> RunAsync(Guid runId, CancellationToken cancellationToken);

    Task<JobRunView> RerunAsync(Guid runId, CancellationToken cancellationToken);

    Task<JobRunView> CancelAsync(Guid runId, CancellationToken cancellationToken);

    Task<JobHealthView> HealthAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The system health page (FR-ADM-OPS-05, Q11: no slow-queries figure) beyond the job numbers: files still waiting for the
/// scan, storage and database size, and the last backup and verified restore drill of this customer only (FR-ADM-DAT-08,
/// Q12; timestamps only, never a path or a secret).
/// </summary>
public sealed record SystemHealthView(
    JobHealthView Jobs,
    int FilesNotScanned,
    DateTimeOffset? OldestUnscannedAt,
    long StorageUsedBytes,
    long DatabaseSizeBytes,
    DateTimeOffset? LastBackupAt,
    DateTimeOffset? LastRestoreTestAt,
    DateTimeOffset At);

/// <summary>Reads the system health page (permission <c>job.runs.read</c>).</summary>
public interface ISystemHealth
{
    Task<SystemHealthView> ReadAsync(CancellationToken cancellationToken);
}
