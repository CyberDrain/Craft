using Craft.Orchestration;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge allowing PowerShell (Get-CIPPQueueData) to query orchestrator/job
/// progress without HTTP round-trips. Returns data in the shape the CIPP frontend expects.
/// PS usage: [Craft.Services.QueueStatusBridge]::GetRunStatus($Reference, $QueueId)
/// </summary>
/// <remarks>
/// Uninitialized policy: status reads soft no-op (empty JSON / empty list);
/// <see cref="RegisterQueueMetadata"/> is safe before Initialize (metadata bag is static).
/// </remarks>
public static class QueueStatusBridge
{
    private static QueueStatusService? s_service;

    public static void Initialize(QueueStatusService service) => s_service = service;

    /// <summary>
    /// Register friendly queue metadata from PowerShell (New-CippQueueEntry).
    /// PS usage: [Craft.Services.QueueStatusBridge]::RegisterQueueMetadata($QueueId, $Name, $Link, $Reference)
    /// </summary>
    public static void RegisterQueueMetadata(string queueId, string name, string link, string reference) =>
        QueueStatusService.RegisterQueueMetadata(queueId, name, link, reference);

    /// <summary>
    /// Get queue/run status in the format expected by the CIPP frontend.
    /// Looks up by run name (Reference) or returns all recent runs.
    /// Returns a JSON string matching the Get-CIPPQueueData output shape.
    /// </summary>
    /// <param name="reference">Optional run reference/name to filter by (maps to RunName in JobManager)</param>
    /// <param name="queueId">Optional queue ID (same as reference in Craft context)</param>
    /// <returns>JSON array of queue status objects</returns>
    public static string GetRunStatus(string? reference = null, string? queueId = null) =>
        s_service?.GetRunStatus(reference, queueId) ?? "[]";
}
