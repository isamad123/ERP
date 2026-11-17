using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Host.Worker;

/// <summary>How often the licence loop runs (<c>Licence:IntervalMinutes</c>, default 60; slice S1.09 ruling Q6).</summary>
public sealed record LicenceLoopOptions(TimeSpan Interval);

/// <summary>
/// The hourly licence loop of the worker host (FR-KRN-ENT-04, FR-KRN-ENT-06; slice S1.09 ruling Q6) until the jobs framework
/// of S1.11 takes it over: records licence-state changes once, puts scheduled entitlements in force on their day, takes the
/// daily usage readings and copies limit events (<see cref="LicenceMaintenance"/>). Business dates come from the injected
/// clock inside the maintenance; only the pause between runs uses machine time. A failure is logged and the next run retries.
/// </summary>
public sealed partial class LicenceLoop(LicenceMaintenance maintenance, LicenceLoopOptions options, TimeProvider time, ILogger<LicenceLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, time);
        do
        {
            try
            {
                foreach (var outcome in await maintenance.RunAsync(null, stoppingToken).ConfigureAwait(false))
                {
                    if (outcome.Error is not null)
                    {
                        Failed(logger, outcome.Code, outcome.Error);
                    }
                    else if (outcome.Changed || outcome.Activated is not null)
                    {
                        Changed(logger, outcome.Code, outcome.PreviousState ?? "-", outcome.State);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Licence refresh of {Code} failed: {Error}")]
    private static partial void Failed(ILogger logger, string code, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Licence of {Code}: {From} -> {To}")]
    private static partial void Changed(ILogger logger, string code, string from, string to);

    [LoggerMessage(Level = LogLevel.Error, Message = "Licence loop run failed")]
    private static partial void RunFailed(ILogger logger, Exception ex);
}
