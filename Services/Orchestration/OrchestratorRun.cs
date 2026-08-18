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
}
