using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Host.Worker;

/// <summary>How often escalation runs (<c>Workflow:EscalationIntervalSeconds</c>, default 60; slice S1.10 ruling Q9).</summary>
public sealed record WorkflowEscalationLoopOptions(TimeSpan Interval);

/// <summary>
/// The escalation loop of the worker host (FR-XM-32, FR-HRM-NTF-11; slice S1.10 ruling Q9) until the jobs framework of S1.11
/// takes it over: every minute, for every served customer, open approval tasks past their time limit move to the next level
/// (<see cref="WorkflowEscalationRunner"/>). Nothing is ever approved automatically. Times come from the injected clock inside
/// the engine; only the pause between runs uses machine time. A failure is logged and the next run retries.
/// </summary>
public sealed partial class WorkflowEscalationLoop(WorkflowEscalationRunner runner, WorkflowEscalationLoopOptions options, TimeProvider time, ILogger<WorkflowEscalationLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, time);
        do
        {
            try
            {
                foreach (var outcome in await runner.RunAsync(null, stoppingToken).ConfigureAwait(false))
                {
                    if (outcome.Error is not null)
                    {
                        Failed(logger, outcome.Code, outcome.Error);
                    }
                    else if (outcome.Run is { } run && (run.Escalated > 0 || run.NotPossible > 0 || run.DueDatesSet > 0))
                    {
                        Escalated(logger, outcome.Code, run.Escalated, run.NotPossible, run.DueDatesSet);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RunFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow escalation of {Code} failed: {Error}")]
    private static partial void Failed(ILogger logger, string code, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow escalation of {Code}: {Escalated} escalated, {NotPossible} with nobody above, {DueDatesSet} time limits set")]
    private static partial void Escalated(ILogger logger, string code, int escalated, int notPossible, int dueDatesSet);

    [LoggerMessage(Level = LogLevel.Error, Message = "Workflow escalation run failed")]
    private static partial void RunFailed(ILogger logger, Exception ex);
}
