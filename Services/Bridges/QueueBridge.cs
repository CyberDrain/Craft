using System.Collections.Concurrent;
using Craft.Orchestration;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Thread-safe bridge allowing PowerShell (Add-CippQueueMessage) to queue
/// background commands that get dispatched on a background worker.
/// Replaces Azure Storage Queue on CIPPNG — purely in-process.
/// </summary>
public static class QueueBridge
{
    private static PowerShellRunnerService? s_runner;
    private static JobManager? s_jobManager;
    private static string? s_queueTaskFunction;
    private static readonly ConcurrentQueue<PendingQueueCommand> s_pending = new();

    public static void Initialize(PowerShellRunnerService runner, JobManager jobManager, string queueTaskFunction)
    {
        s_runner = runner;
        s_jobManager = jobManager;
        s_queueTaskFunction = queueTaskFunction;
    }

    public static void Enqueue(string cmdlet, string parametersJson)
    {
        s_pending.Enqueue(new PendingQueueCommand(cmdlet, parametersJson));
    }

    public static void DrainPending()
    {
        if (string.IsNullOrEmpty(s_queueTaskFunction)) return;

        while (s_pending.TryDequeue(out var cmd))
        {
            var scriptPath = s_runner?.FindScript(s_queueTaskFunction);
            if (scriptPath == null || s_runner == null || s_jobManager == null)
                continue;

            var captured = cmd;
            s_jobManager.Enqueue(
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
                    await s_runner.ExecuteScript(scriptPath, parameters);

                    // Queued commands may trigger orchestrators
                    await OrchestratorBridge.DrainPendingAsync();
                }
            );
        }
    }

    public record PendingQueueCommand(string Cmdlet, string ParametersJson);
}
