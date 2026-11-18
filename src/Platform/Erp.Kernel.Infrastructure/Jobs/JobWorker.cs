using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Jobs;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>
/// Settings of one worker (slice S1.11 ruling Q5): at most <see cref="Slots"/> runs at once (<c>Jobs:Slots</c>, 4), at most
/// <see cref="PerTenantSlots"/> of one customer (<c>Jobs:PerTenantSlots</c>, 2); a run's lease lasts <see cref="Lease"/>
/// (<c>Jobs:LeaseSeconds</c>, 60) and is renewed every third of it.
/// </summary>
public sealed record JobWorkerOptions(string WorkerId, int Slots = 4, int PerTenantSlots = 2, TimeSpan? LeaseTime = null)
{
    public TimeSpan Lease => LeaseTime ?? TimeSpan.FromSeconds(60);

    /// <summary>A worker id that differs between processes and machines (shown as the run's lease owner).</summary>
    public static string NewWorkerId() => string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
}

/// <summary>What one pass of the scheduler did.</summary>
public sealed record JobPollOutcome(int Planned, int Started, IReadOnlyList<string> Errors);

/// <summary>
/// The scheduler and runner of the worker host (slice S1.11 Build item 2; FR-KRN-JOB-02, FR-KRN-JOB-04, FR-KRN-JOB-05;
/// rulings Q2, Q5, Q6). Each pass, for every customer it may serve:
/// <list type="number">
/// <item>queues the latest due slot of every enabled schedule once (a unique slot, so several workers plan it once);</item>
/// <item>takes runs under a lease, customers in turn, highest priority first, within the worker's slots and the
/// per-customer cap, so one customer's long queue never starves another;</item>
/// <item>runs each in a service scope bound to the customer, acting as the job actor or the requester (R-009).</item>
/// </list>
/// A run whose worker dies is taken over by another worker once its lease expires and continues from its last checkpoint.
/// Times of leases, slots and retries come from the injected clock; only the pauses between renewals use machine time.
/// Jobs run only in the access modes their manifest names and only when their feature is licensed (in locked and
/// suspended mode only the jobs marked for them run, so the licence is not asked).
/// </summary>
public sealed class JobWorker(
    ControlPlaneOptions controlPlane,
    ITenantDataSourceFactory dataSources,
    ILicenceResolver licences,
    IServiceScopeFactory scopes,
    IClock clock,
    JobCatalog catalog,
    JobWorkerOptions options) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<ActiveRun> _active = [];
    private readonly CancellationTokenSource _stopping = new();
    private int _turn;

    public JobWorkerOptions Options => options;

    /// <summary>Runs this worker is executing now.</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count(a => !a.Task.IsCompleted);
            }
        }
    }

    /// <summary>One scheduler pass (all served customers, or one).</summary>
    public Task<JobPollOutcome> PollOnceAsync(string? onlyTenantCode, CancellationToken cancellationToken) =>
        PollOnceAsync(onlyTenantCode is null ? null : [onlyTenantCode], cancellationToken);

    /// <summary>One scheduler pass over all served customers, or only the named ones (operator and tests).</summary>
    public async Task<JobPollOutcome> PollOnceAsync(IReadOnlyCollection<string>? onlyTenantCodes, CancellationToken cancellationToken)
    {
        Reap();
        var errors = new List<string>();
        var planned = 0;
        var started = 0;
        var tenants = new List<TenantRun>();
        var served = await TenantsAsync(onlyTenantCodes is { Count: 1 } one ? one.First() : null, cancellationToken).ConfigureAwait(false);
        foreach (var tenant in served.Where(t => onlyTenantCodes is null || onlyTenantCodes.Contains(t.Code)))
        {
            try
            {
                var bound = await BindAsync(tenant, cancellationToken).ConfigureAwait(false);
                if (bound is null)
                {
                    continue;
                }

                planned += await PlanAsync(bound, cancellationToken).ConfigureAwait(false);
                tenants.Add(bound);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{tenant.Code}: {ex.Message}");
            }
        }

        if (tenants.Count == 0)
        {
            return new JobPollOutcome(planned, 0, errors);
        }

        // Customers in turn (ruling Q5): each round gives every customer with room one run, starting one further along each pass.
        var offset = Interlocked.Increment(ref _turn);
        var order = Enumerable.Range(0, tenants.Count).Select(i => tenants[(i + offset) % tenants.Count]).ToList();
        var exhausted = new HashSet<string>(StringComparer.Ordinal);
        bool progress;
        do
        {
            progress = false;
            foreach (var t in order)
            {
                if (FreeSlots() == 0)
                {
                    break;
                }

                if (exhausted.Contains(t.Tenant.Code) || ActiveOf(t.Tenant.Code) >= options.PerTenantSlots)
                {
                    continue;
                }

                try
                {
                    ClaimedRun? run;
                    await using (var c = (NpgsqlConnection)await t.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        run = await JobStore.ClaimAsync(c, t.Runnable, options.WorkerId, clock.UtcNow, options.Lease, cancellationToken).ConfigureAwait(false);
                    }

                    if (run is null)
                    {
                        exhausted.Add(t.Tenant.Code);
                        continue;
                    }

                    Start(t, run);
                    started++;
                    progress = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{t.Tenant.Code}: {ex.Message}");
                    exhausted.Add(t.Tenant.Code);
                }
            }
        }
        while (progress && FreeSlots() > 0);

        return new JobPollOutcome(planned, started, errors);
    }

    /// <summary>Waits until every run this worker started has ended (tests and graceful stop).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_active.Select(a => a.Task));
        }
    }

    /// <summary>
    /// Polls and waits until no run starts any more (tests and the operator: one customer's due work, done now).
    /// </summary>
    public async Task<int> RunUntilIdleAsync(string? onlyTenantCode, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var pass = 0; pass < 100; pass++)
        {
            var outcome = await PollOnceAsync(onlyTenantCode, cancellationToken).ConfigureAwait(false);
            if (outcome.Errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", outcome.Errors));
            }

            await WhenIdleAsync().ConfigureAwait(false);
            total += outcome.Started;
            if (outcome.Started == 0)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Stops at once without ending the runs (as a crashed worker would): their leases expire and another worker takes
    /// them over from their last checkpoint.
    /// </summary>
    public async Task KillAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await WhenIdleAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    // ------------------------------------------------------------------ customers

    private sealed record TenantRun(ResolvedTenant Tenant, DbDataSource DataSource, LicenceSnapshot Licence, TenantAccessMode Mode, string ModeName, IReadOnlyCollection<string> Runnable);

    private Task<List<ResolvedTenant>> TenantsAsync(string? only, CancellationToken ct) => ServedTenants.ListAsync(controlPlane, only, ct);

    private async Task<TenantRun?> BindAsync(ResolvedTenant tenant, CancellationToken ct)
    {
        var dataSource = await dataSources.GetAsync(tenant, ct).ConfigureAwait(false);
        var licence = await licences.ResolveAsync(tenant, dataSource, ct).ConfigureAwait(false);
        var mode = TenantAccessPolicy.Decide(tenant.Status, tenant.DatabaseStatus, licence.State);
        var name = mode switch
        {
            TenantAccessMode.Full => JobModes.Full,
            TenantAccessMode.ReadOnly => JobModes.ReadOnly,
            TenantAccessMode.Locked when tenant.Status == "suspended" => JobModes.Suspended,
            TenantAccessMode.Locked => JobModes.Locked,
            _ => null,
        };
        if (name is null)
        {
            return null;
        }

        var askLicence = name is JobModes.Full or JobModes.ReadOnly;
        var runnable = catalog.All.Where(s => s.RunsIn.Contains(name) && (!askLicence || licence.IsLicensed(s.Entitlement))).Select(s => s.Code).ToList();
        return new TenantRun(tenant, dataSource, licence, mode, name, runnable);
    }

    // ------------------------------------------------------------------ planning

    private async Task<int> PlanAsync(TenantRun t, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var c = (NpgsqlConnection)await t.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var scheduled = (await JobStore.ScheduledAsync(c, ct).ConfigureAwait(false)).Where(d => t.Runnable.Contains(d.Code)).ToList();
        if (scheduled.Count == 0)
        {
            return 0;
        }

        var schedules = scheduled.Select(d => (d.Id, d.Priority, Schedule: ParseOrNull(d.Schedule))).Where(d => d.Schedule is not null).ToList();
        var calendars = new Dictionary<Guid, JobCalendar>();
        var customer = await JobStore.CalendarAsync(c, null, today, ct).ConfigureAwait(false);
        foreach (var company in schedules.Select(s => s.Schedule!.CalendarCompanyId).OfType<Guid>().Distinct())
        {
            calendars[company] = await JobStore.CalendarAsync(c, company, today, ct).ConfigureAwait(false);
        }

        var planned = 0;
        foreach (var (id, priority, schedule) in schedules)
        {
            var calendar = schedule!.CalendarCompanyId is { } company ? calendars[company] : customer;
            if (schedule.Latest(now, calendar.Zone, calendar.IsWorkingDay) is { } slot)
            {
                planned += await JobStore.PlanSlotAsync(c, id, priority, slot, now, ct).ConfigureAwait(false);
            }
        }

        return planned;
    }

    /// <summary>A stored schedule that cannot be read plans nothing (the job centre refuses such schedules; a manual edit is reported by the alerts).</summary>
    private static JobSchedule? ParseOrNull(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JobSchedule.Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ running

    private sealed record ActiveRun(string TenantCode, Guid RunId, Task Task);

    private int FreeSlots()
    {
        lock (_gate)
        {
            return Math.Max(0, options.Slots - _active.Count(a => !a.Task.IsCompleted));
        }
    }

    private int ActiveOf(string code)
    {
        lock (_gate)
        {
            return _active.Count(a => a.TenantCode == code && !a.Task.IsCompleted);
        }
    }

    private void Reap()
    {
        lock (_gate)
        {
            _active.RemoveAll(a => a.Task.IsCompleted);
        }
    }

    private void Start(TenantRun t, ClaimedRun run)
    {
        var task = Task.Run(() => ExecuteAsync(t, run, _stopping.Token));
        lock (_gate)
        {
            _active.Add(new ActiveRun(t.Tenant.Code, run.Id, task));
        }
    }

    private async Task ExecuteAsync(TenantRun t, ClaimedRun run, CancellationToken stopping)
    {
        var spec = catalog.Find(run.Code)!;
        await using var c = (NpgsqlConnection)await t.DataSource.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        Guid? owner = await JobStore.OwnerOfAsync(c, run.DefinitionId, CancellationToken.None).ConfigureAwait(false);
        if (run.Attempt > spec.MaxAttempts)
        {
            // Taken over after the worker of the last allowed attempt stopped: not started again.
            await JobStore.FailAsync(c, run.Id, options.WorkerId, run.Code, owner, JobErrors.WorkerStopped, clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await JobStore.LogAsync(c, null, run.Id, "info", string.Create(CultureInfo.InvariantCulture, $"attempt {run.Attempt} started by {options.WorkerId}{(run.TakenOver ? " (taken over after the previous worker stopped)" : string.Empty)}"), clock.UtcNow, CancellationToken.None).ConfigureAwait(false);

        using var leaseLost = new CancellationTokenSource();
        using var timeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, leaseLost.Token, timeLimit.Token);
        using var renewing = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var renewal = RenewLoopAsync(t.DataSource, run.Id, leaseLost, renewing.Token);
        string? failure = null;
        var retry = true;
        try
        {
            // Hand-written services read the audit context from the ambient value (ruling P9): source job, the run as correlation.
            AuditAmbient.Current = new AuditRequestInfo(run.Id, null, AuditSources.Job);
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(t.Tenant, t.Mode, t.DataSource, t.Licence);
            var job = scope.ServiceProvider.GetRequiredService<JobRunContext>();
            if (spec.RunsAs == JobActors.Requester && run.ActorUserId is { } person)
            {
                job.BindRequester(run.Id, run.Code, person);
                if (await MissingRightAsync(scope.ServiceProvider, spec, run, linked.Token).ConfigureAwait(false) is { } missing)
                {
                    failure = JobErrors.RequesterLostPermission + ": " + missing;
                    retry = false;
                }
            }
            else
            {
                job.BindJobActor(run.Id, run.Code, spec.Permissions);
            }

            if (failure is null)
            {
                var execution = new JobExecution(t.DataSource, clock, options, run, scope.ServiceProvider);
                await catalog.Handler(run.Code).RunAsync(execution, linked.Token).ConfigureAwait(false);
                await renewing.CancelAsync().ConfigureAwait(false);
                await renewal.ConfigureAwait(false);
                if (await JobStore.FinishAsync(c, null, run.Id, options.WorkerId, "succeeded", clock.UtcNow, CancellationToken.None).ConfigureAwait(false) == 1)
                {
                    await JobStore.LogAsync(c, null, run.Id, "info", "succeeded", clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }
        }
        catch (JobStoppedException ex) when (ex.Message == JobStoppedException.Cancelled)
        {
            await renewing.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
            if (await JobStore.FinishAsync(c, null, run.Id, options.WorkerId, "cancelled", clock.UtcNow, CancellationToken.None).ConfigureAwait(false) == 1)
            {
                await JobStore.LogAsync(c, null, run.Id, "warning", "cancelled at a chunk boundary", clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }
        catch (Exception ex) when (stopping.IsCancellationRequested || leaseLost.IsCancellationRequested || ex is JobStoppedException)
        {
            // The worker is stopping, or another worker holds the run now: leave the run to its lease.
            await renewing.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (timeLimit.IsCancellationRequested)
        {
            failure = JobErrors.TimeLimit;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        await renewing.CancelAsync().ConfigureAwait(false);
        await renewal.ConfigureAwait(false);
        var now = clock.UtcNow;
        await JobStore.LogAsync(c, null, run.Id, "error", string.Create(CultureInfo.InvariantCulture, $"attempt {run.Attempt} failed: {failure}"), now, CancellationToken.None).ConfigureAwait(false);
        if (retry && run.Attempt < spec.MaxAttempts)
        {
            await JobStore.RetryLaterAsync(c, null, run.Id, options.WorkerId, failure!, now + spec.Backoff(run.Attempt), now, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await JobStore.FailAsync(c, run.Id, options.WorkerId, run.Code, owner, failure!, now, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>A requested run acts with the requester's rights, checked again when it starts (ruling Q4).</summary>
    private static async Task<string?> MissingRightAsync(IServiceProvider services, JobSpec spec, ClaimedRun run, CancellationToken ct)
    {
        var access = services.GetRequiredService<ICallerAccess>();
        foreach (var permission in spec.Permissions.Append(spec.RequestPermission).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var holds = run.CompanyId is { } company
                ? await access.CoversCompanyAsync(permission, company, ct).ConfigureAwait(false)
                : await access.HoldsAsync(permission, ct).ConfigureAwait(false);
            if (!holds)
            {
                return permission;
            }
        }

        return null;
    }

    private async Task RenewLoopAsync(DbDataSource dataSource, Guid run, CancellationTokenSource leaseLost, CancellationToken ct)
    {
        var every = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond * 50, options.Lease.Ticks / 3));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(every, ct).ConfigureAwait(false);
                await using var c = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                if (!await JobStore.RenewAsync(c, null, run, options.WorkerId, clock.UtcNow, options.Lease, ct).ConfigureAwait(false))
                {
                    await leaseLost.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (NpgsqlException)
        {
            // A failed renewal is tried again at the next tick; the chunk guard refuses work once the lease is really gone.
        }
    }
}

/// <summary>Reasons of the framework itself for a failed run (localization keys).</summary>
public static class JobErrors
{
    public const string TimeLimit = "errors.job.time_limit";
    public const string WorkerStopped = "errors.job.worker_stopped";
    public const string RequesterLostPermission = "errors.job.requester_lost_permission";
}

/// <summary>The run as its handler sees it (slice S1.11 Build item 2).</summary>
internal sealed class JobExecution : IJobExecution
{
    private readonly DbDataSource _dataSource;
    private readonly IClock _clock;
    private readonly JobWorkerOptions _options;
    private JsonElement? _checkpoint;

    public JobExecution(DbDataSource dataSource, IClock clock, JobWorkerOptions options, ClaimedRun run, IServiceProvider services)
    {
        Services = services;
        _dataSource = dataSource;
        _clock = clock;
        _options = options;
        RunId = run.Id;
        Code = run.Code;
        CompanyId = run.CompanyId;
        Attempt = run.Attempt;
        ScheduledFor = run.ScheduledFor;
        using (var doc = JsonDocument.Parse(run.Parameters ?? "{}"))
        {
            Parameters = doc.RootElement.Clone();
        }

        if (run.Checkpoint is { } checkpoint)
        {
            using var doc = JsonDocument.Parse(checkpoint);
            _checkpoint = doc.RootElement.Clone();
        }
    }

    public Guid RunId { get; }

    public string Code { get; }

    public Guid? CompanyId { get; }

    public JsonElement Parameters { get; }

    public JsonElement? Checkpoint => _checkpoint;

    public int Attempt { get; }

    public DateTimeOffset? ScheduledFor { get; }

    public IServiceProvider Services { get; }

    public async Task CommitChunkAsync(Func<DbConnection, DbTransaction, CancellationToken, Task>? work, object? checkpoint, decimal? progressPct, CancellationToken cancellationToken)
    {
        await using var c = (NpgsqlConnection)await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (held, cancelled) = await JobStore.HoldAsync(c, tx, RunId, _options.WorkerId, cancellationToken).ConfigureAwait(false);
        if (!held)
        {
            throw new JobStoppedException(JobStoppedException.LeaseLost);
        }

        if (cancelled)
        {
            throw new JobStoppedException(JobStoppedException.Cancelled);
        }

        if (work is not null)
        {
            await work(c, tx, cancellationToken).ConfigureAwait(false);
        }

        var text = checkpoint switch
        {
            null => null,
            JsonElement e => e.GetRawText(),
            _ => JsonSerializer.Serialize(checkpoint),
        };
        await JobStore.SaveCheckpointAsync(c, tx, RunId, text, progressPct is { } p ? Math.Clamp(p, 0, 100) : null, _clock.UtcNow, _options.Lease, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (text is not null)
        {
            using var doc = JsonDocument.Parse(text);
            _checkpoint = doc.RootElement.Clone();
        }
    }

    public async Task LogAsync(string level, string message, CancellationToken cancellationToken)
    {
        await using var c = (NpgsqlConnection)await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await JobStore.LogAsync(c, null, RunId, level is "info" or "warning" or "error" ? level : "info", message, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}
