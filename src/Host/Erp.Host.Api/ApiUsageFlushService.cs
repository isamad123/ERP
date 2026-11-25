using Erp.Kernel.Infrastructure.Access;

namespace Erp.Host.Api;

/// <summary>
/// Adds this host's API client counts to the database and reads back the other hosts' counts, every minute (slice S1.27c Build items 12 and 13, risk R-208).
/// <c>Api:UsageFlushMilliseconds</c> changes the interval; 0 switches the loop off (tests flush on demand through the meter).
/// </summary>
public sealed class ApiUsageFlushService(ApiUsageRecorder recorder, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var milliseconds = configuration.GetValue("Api:UsageFlushMilliseconds", 60_000);
        if (milliseconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(milliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await recorder.FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }

        await recorder.FlushAsync(CancellationToken.None).ConfigureAwait(false); // the last minute's counts are not lost on a normal stop
    }
}
