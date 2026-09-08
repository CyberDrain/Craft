namespace Craft.Orchestration;

public class OrchestratorRun
{
    public string Name { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string Status { get; set; } = "Pending";
    // 4 matches what every live enqueue path actually passes when a caller sets nothing — a run row
    // rehydrated without a stored priority must not come back HIGHER than it originally ran.
    public int Priority { get; set; } = 4;
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public List<OrchestratorTaskItem> Tasks { get; set; } = [];
    public string? TaskScriptName { get; set; }
    public string? PostExecFunctionName { get; set; }
    public string? PostExecParametersJson { get; set; }
    // null | "Pending" | "Running" | "Completed" | "Failed" | "Abandoned"
    // "Failed" is retryable — recovery picks it back up on the next host start. "Abandoned" is the
    // terminal one: retries are spent, and the run's result rows have been cleaned up.
    public string? PostExecStatus { get; set; }

    /// <summary>
    /// How many times post-execution has been attempted. Bounds the retry that
    /// ResumeInterruptedRunsAsync performs for a "Failed" post-execution, the same way
    /// <see cref="OrchestratorTaskItem.AttemptCount"/> bounds task recovery — without it a
    /// permanently-failing aggregation would be retried on every host start forever.
    /// </summary>
    public int PostExecAttemptCount { get; set; }

    public string? ParentRunName { get; set; }

    /// <summary>
    /// Sequential execution mode. When true the run's tasks are dispatched ONE AT A TIME, in ascending
    /// <see cref="OrchestratorTaskItem.Sequence"/> (batch) order: only the current task is ever enqueued,
    /// and the next is enqueued when it reaches a terminal state. Runs on any free worker (no pinning) —
    /// the durable queue simply never holds more than one of this run's tasks at once. The default (false)
    /// is the fan-out behaviour: every task is enqueued up front and drained in parallel by the pool.
    /// </summary>
    public bool Sequential { get; set; }
}
