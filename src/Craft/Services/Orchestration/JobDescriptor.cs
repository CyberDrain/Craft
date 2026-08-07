namespace Craft.Orchestration;

/// <summary>
/// The identity of a queued orchestrator task — everything the queue needs to hold, and nothing more.
///
/// Orchestrator fan-out is where queue depth comes from (production peaked at 783 queued with 3.7-hour
/// waits), and it used to enqueue a closure capturing the whole <see cref="OrchestratorRun"/> graph, the
/// task, the script path and the service. A descriptor replaces all of that with two string references
/// and an int; <see cref="JobWorkResolver"/> turns it back into runnable work at dispatch time.
/// </summary>
/// <param name="RunName">Run this task belongs to — the storage partition key.</param>
/// <param name="TaskId">Task id within the run — the storage row key.</param>
/// <param name="Priority">Dispatch priority (lower = higher).</param>
public readonly record struct JobDescriptor(string RunName, string TaskId, int Priority);

/// <summary>
/// Rehydrates a <see cref="JobDescriptor"/> into runnable work. Returns null when the descriptor is
/// stale — the task no longer exists, or already reached a terminal state while it sat queued — which
/// the JobManager records as "Skipped" rather than as a failure.
///
/// Invoked from the dispatched task, never from the dispatch loop, because it may do storage I/O.
/// </summary>
public delegate Task<Func<CancellationToken, Task>?> JobWorkResolver(JobDescriptor descriptor, CancellationToken ct);

/// <summary>
/// Persists operator-initiated changes to a QUEUED descriptor job, so they survive a restart.
///
/// Without this, both operations were in-memory only and silently undone by a container restart —
/// which, at nine restarts in eleven hours, means routinely. A reprioritized job reverted to its run's
/// priority, and a cancelled job CAME BACK, because recovery re-queues every task the storage layer
/// still reports as Pending.
///
/// Closure jobs (simple scheduled scripts, post-execution) are deliberately not reported: they carry
/// no descriptor and are never recovered from storage — a scheduled script simply re-fires on its next
/// cron tick — so there is nothing to make durable.
/// </summary>
public interface IJobDescriptorStateWriter
{
    /// <summary>An operator changed this queued task's dispatch priority.</summary>
    void PriorityChanged(JobDescriptor descriptor, int newPriority);

    /// <summary>An operator cancelled this queued task before it ran.</summary>
    void Cancelled(JobDescriptor descriptor);
}
