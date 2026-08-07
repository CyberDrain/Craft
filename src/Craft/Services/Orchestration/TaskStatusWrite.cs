namespace Craft.Orchestration;

/// <summary>
/// An immutable snapshot of a task's status for the coalescing batched writer.
///
/// Every column the task row carries must appear here. The store upserts with
/// <c>TableUpdateMode.Replace</c>, so any column missing from this snapshot is ERASED by the next
/// status transition — which is how a per-task Priority override would silently vanish the first
/// time the task moved to Running.
/// </summary>
public record TaskStatusWrite(string RunName, string TaskId, string Status, string? ParametersJson,
    int AttemptCount, string? LastError, DateTime? CompletedUtc, int? Priority);
