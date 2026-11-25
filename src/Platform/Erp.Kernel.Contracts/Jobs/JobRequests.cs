using System.Data.Common;
using System.Text.Json;

namespace Erp.Kernel.Contracts.Jobs;

/// <summary>
/// A run a module asks for on behalf of a person (slice S1.35b, CR-093 S1.35b detail): the job code of a module's manifest, the company it is for (or
/// null), its parameters (an object) and the person who asked. A job whose manifest says <c>runs_as: requester</c> runs with that person's rights,
/// checked again when it starts (S1.11a ruling Q4).
/// </summary>
public sealed record JobRequest(string Code, Guid? CompanyId, JsonElement Parameters, Guid RequestedBy);

/// <summary>Where a run stands: its status (queued, running, succeeded, failed, cancelled), progress, attempt, times and error key.</summary>
public sealed record JobRunState(Guid Id, string Code, string Status, decimal? ProgressPct, int Attempt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset? CancelRequestedAt, string? Error);

/// <summary>
/// Runs a module starts from its own service (slice S1.35b: the payroll calculation's job). The module checks its own permission first; this contract
/// checks none. With a transaction, the queue and the cancel take part in the caller's open PostgreSQL transaction (the same customer database), so the
/// module's own change and the run commit together or not at all.
/// </summary>
public interface IJobRequests
{
    /// <summary>Queues one run and returns its id; throws <see cref="InvalidOperationException"/> when the job code is not seeded.</summary>
    Task<Guid> QueueAsync(JobRequest request, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>Asks the runs to stop: a queued run is cancelled at once, a running one at its next chunk. Returns how many runs were asked.</summary>
    Task<int> CancelAsync(IReadOnlyCollection<Guid> runIds, Guid by, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>The state of the runs found (a run id not found is left out).</summary>
    Task<IReadOnlyList<JobRunState>> StatesAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken);
}
