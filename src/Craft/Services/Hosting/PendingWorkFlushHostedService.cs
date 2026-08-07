using Craft.Orchestration;
using Craft.Services;

namespace Craft.Hosting;

/// <summary>
/// On host shutdown: final drain of PowerShell ingress queues, then await tracked
/// fire-and-forget drain/planner/finalize work with a bounded timeout.
/// </summary>
internal sealed class PendingWorkFlushHostedService : IHostedService
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(30);

    private readonly PowerShellRunnerService _runner;
    private readonly OrchestratorService _orchestrator;
    private readonly QueueDispatchService _queueDispatch;
    private readonly ILogger<PendingWorkFlushHostedService> _logger;

    public PendingWorkFlushHostedService(
        PowerShellRunnerService runner,
        OrchestratorService orchestrator,
        QueueDispatchService queueDispatch,
        ILogger<PendingWorkFlushHostedService> logger)
    {
        _runner = runner;
        _orchestrator = orchestrator;
        _queueDispatch = queueDispatch;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Shutdown] Flushing pending orchestration/queue work");

        try
        {
            await _orchestrator.DrainPendingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Shutdown] Final orchestrator DrainPending failed");
        }

        try
        {
            _queueDispatch.DrainPending();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Shutdown] Final queue DrainPending failed");
        }

        // Prefer a linked token that still respects host abort, but cap wait ourselves.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FlushTimeout);

        int runnerLeft = 0, orchLeft = 0;
        try
        {
            runnerLeft = await _runner.FlushBackgroundDrainsAsync(FlushTimeout, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            runnerLeft = -1;
            _logger.LogWarning("[Shutdown] Runner background-drain flush timed out after {Timeout}", FlushTimeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Shutdown] Runner background-drain flush failed");
        }

        try
        {
            orchLeft = await _orchestrator.FlushBackgroundWorkAsync(FlushTimeout, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            orchLeft = -1;
            _logger.LogWarning("[Shutdown] Orchestrator background-work flush timed out after {Timeout}", FlushTimeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Shutdown] Orchestrator background-work flush failed");
        }

        if (runnerLeft > 0 || orchLeft > 0)
            _logger.LogWarning(
                "[Shutdown] Pending work flush incomplete — runnerLeftovers={Runner} orchLeftovers={Orch}",
                runnerLeft, orchLeft);
        else
            _logger.LogInformation("[Shutdown] Pending work flush complete");
    }
}
