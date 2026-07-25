namespace Craft.Storage;

/// <summary>An immutable snapshot of a task's status for the coalescing batched writer.</summary>
public record TaskStatusWrite(string RunName, string TaskId, string Status, string? ParametersJson,
    int AttemptCount, string? LastError, DateTime? CompletedUtc);
