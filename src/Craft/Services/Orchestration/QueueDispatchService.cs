using System.Collections.Concurrent;
using Craft.Configuration;
using Craft.Services;

namespace Craft.Orchestration;

/// <summary>
/// In-process queue for PowerShell <c>Add-CippQueueMessage</c> style background commands.
/// Domain code drains via this service; PowerShell enqueues through <c>QueueBridge</c>.
/// </summary>
public sealed class QueueDispatchService
{
    private readonly PowerShellRunnerService _runner;
    private readonly JobManager _jobManager;
    private readonly OrchestratorService _orchestrator;
    private readonly ILogger<QueueDispatchService> _logger;
    private readonly string _queueTaskFunction;
    private readonly ConcurrentQueue<PendingQueueCommand> _pending = new();

    public QueueDispatchService(
        PowerShellRunnerService runner,
        JobManager jobManager,
        OrchestratorService orchestrator,
        CraftSettings settings,
        ILogger<QueueDispatchService> logger)
    {
        _runner = runner;
        _jobManager = jobManager;
        _orchestrator = orchestrator;
        _logger = logger;
        _queueTaskFunction = settings.Orchestrator.QueueTaskFunction;
    }

    public void Enqueue(string cmdlet, string parametersJson) =>
        _pending.Enqueue(new PendingQueueCommand(cmdlet, parametersJson));

    public void DrainPending()
    {
        if (string.IsNullOrEmpty(_queueTaskFunction))
        {
            if (!_pending.IsEmpty)
            {
                var dropped = 0;
                while (_pending.TryDequeue(out _)) dropped++;
                _logger.LogWarning(
                    "[Queue] Dropping {Count} pending command(s) — App:Orchestrator:QueueTaskFunction is not configured",
                    dropped);
            }
            return;
        }

        while (_pending.TryDequeue(out var cmd))
        {
            var scriptPath = _runner.FindScript(_queueTaskFunction);
            if (scriptPath == null)
            {
                _logger.LogWarning(
                    "[Queue] Dropping command {Cmdlet} — queue task function '{Function}' was not found",
                    cmd.Cmdlet, _queueTaskFunction);
                continue;
            }

            var captured = cmd;
            _jobManager.Enqueue(
                name: $"Queue-{captured.Cmdlet}",
                priority: 5,
                runName: $"Queue-{captured.Cmdlet}-{Guid.NewGuid():N}",
                id: $"Queue-{Guid.NewGuid():N}",
                work: async (ct) =>
                {
                    var parameters = new Dictionary<string, object>
                    {
                        { "Cmdlet", captured.Cmdlet },
                        { "ParametersJson", captured.ParametersJson }
                    };
                    await _runner.ExecuteScript(scriptPath, parameters);

                    // Queued commands may trigger orchestrators
                    await _orchestrator.DrainPendingAsync();
                }
            );
        }
    }
}
