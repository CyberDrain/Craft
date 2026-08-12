// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class JobSummary
{
    /// <summary>Everything waiting to run: this instance's buffer plus the unclaimed durable backlog.</summary>
    public int Queued { get; set; }

    /// <summary>Jobs buffered in this instance's JobManager (claimed rows plus closure jobs).</summary>
    public int QueuedLocal { get; set; }

    /// <summary>Unclaimed rows in the durable queue table — the backlog no instance has taken yet.</summary>
    public int QueuedDurable { get; set; }

    public int Running { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public long TotalProcessed { get; set; }
    public DateTime? OldestQueuedUtc { get; set; }
    public int MaxConcurrency { get; set; }
    public int ActiveConcurrency { get; set; }
}
