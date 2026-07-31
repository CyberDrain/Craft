namespace Craft.Orchestration;

public class OrchestratorTaskItem
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public Dictionary<string, object> Parameters { get; set; } = [];
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>
    /// Dispatch priority override for this task alone. Null — the normal case — means "inherit the
    /// run's priority", so existing rows and everything the planner emits behave exactly as before
    /// with no backfill needed.
    ///
    /// Set only when an operator reprioritizes one queued job (<c>JobManager.ChangePriority</c>).
    /// Persisted so the override survives a restart, instead of silently reverting to the run's
    /// priority when <c>ResumeInterruptedRunsAsync</c> re-queues the task.
    /// </summary>
    public int? Priority { get; set; }
}
