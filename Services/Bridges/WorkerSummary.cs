// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class WorkerSummary
{
    public int HttpBusy { get; set; }
    public int HttpAvailable { get; set; }
    public int HttpPoolSize { get; set; }
    public int BgBusy { get; set; }
    public int BgAvailable { get; set; }
    public int BgPoolSize { get; set; }
    public int LimiterActive { get; set; }
    public int LimiterWaiting { get; set; }
    public int LimiterMax { get; set; }
    public bool IsHttpThrottled { get; set; }
    public int JobsQueued { get; set; }
    public int JobsQueuedLocal { get; set; }
    public int JobsQueuedDurable { get; set; }
    public int JobsActive { get; set; }
}
