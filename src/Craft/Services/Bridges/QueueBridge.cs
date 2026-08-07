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
/// <remarks>
/// Uninitialized policy: <see cref="Enqueue"/> throws; <see cref="DrainPending"/> soft no-ops.
/// </remarks>
public static class QueueBridge
{
    private static QueueDispatchService? s_dispatch;

    internal static void Initialize(QueueDispatchService dispatch) => s_dispatch = dispatch;

    public static void Enqueue(string cmdlet, string parametersJson)
    {
        var dispatch = s_dispatch ?? throw new InvalidOperationException("QueueBridge not initialized");
        dispatch.Enqueue(cmdlet, parametersJson);
    }

    public static void DrainPending() =>
        s_dispatch?.DrainPending();
}
