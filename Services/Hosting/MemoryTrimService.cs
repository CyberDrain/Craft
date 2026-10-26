using Craft.Configuration;
using Craft.Orchestration;
using Craft.Services;

namespace Craft.Hosting;

/// <summary>Runs <see cref="WorkerMetricsBridge.TrimMemory"/> on a fixed interval (Worker.MemoryTrimIntervalMinutes).</summary>
public class MemoryTrimService(ILogger<MemoryTrimService> logger, CraftSettings settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = settings.Worker.MemoryTrimIntervalMinutes;
        if (minutes <= 0) return;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // An exception escaping ExecuteAsync stops the host; a failed trim waits for the next tick.
                try
                {
                    var reclaimed = WorkerMetricsBridge.TrimMemory();
                    if (reclaimed >= 0)
                        logger.LogInformation("[System] Memory trim (timer): reclaimed ~{MB}MB {Memory}",
                            reclaimed, BackgroundTaskLimiter.GetMemorySnapshot());
                }
                catch (Exception ex)
                {
                    try { logger.LogWarning(ex, "[System] Memory trim (timer) failed; retrying next tick"); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
