using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Host.Worker;

/// <summary>
/// The scheduler loop of the worker host (slice S1.11 Build item 2, ruling Q5): one <see cref="JobWorker"/> pass every
/// <c>Jobs:PollMilliseconds</c>. On stop the runs in progress are left to their leases and continue on another worker.
/// </summary>
public sealed partial class JobWorkerService(JobWorker worker, WorkerHost.LoopOptions options, TimeProvider time, ILogger<JobWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Started(logger, worker.Options.WorkerId, worker.Options.Slots, worker.Options.PerTenantSlots);
        using var timer = new PeriodicTimer(options.JobPoll, time);
        try
        {
            do
            {
                try
                {
                    var outcome = await worker.PollOnceAsync((IReadOnlyCollection<string>?)null, stoppingToken).ConfigureAwait(false);
                    foreach (var error in outcome.Errors)
                    {
                        PassFailed(logger, error);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LoopFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        finally
        {
            await worker.KillAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job worker {WorkerId} started ({Slots} slots, {PerTenant} per customer)")]
    private static partial void Started(ILogger logger, string workerId, int slots, int perTenant);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job scheduler: {Error}")]
    private static partial void PassFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job scheduler pass failed")]
    private static partial void LoopFailed(ILogger logger, Exception ex);
}

/// <summary>The event dispatcher loop of the worker host (slice S1.11 Build item 4, ruling Q7), every <c>Events:PollMilliseconds</c>.</summary>
public sealed partial class EventDispatcherService(EventDispatcher dispatcher, WorkerHost.LoopOptions options, TimeProvider time, ILogger<EventDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.EventPoll, time);
        do
        {
            try
            {
                foreach (var outcome in await dispatcher.DispatchOnceAsync(null, stoppingToken).ConfigureAwait(false))
                {
                    if (outcome.Error is not null)
                    {
                        Failed(logger, outcome.Code, outcome.Error);
                    }
                    else if (outcome.Failed > 0)
                    {
                        Retrying(logger, outcome.Code, outcome.Failed);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoopFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event dispatch of {Code} failed: {Error}")]
    private static partial void Failed(ILogger logger, string code, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event dispatch of {Code}: {Count} event(s) will be retried")]
    private static partial void Retrying(ILogger logger, string code, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event dispatch pass failed")]
    private static partial void LoopFailed(ILogger logger, Exception ex);
}
