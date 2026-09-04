namespace Craft.Storage;

/// <summary>
/// A small task result queued for the coalescing status writer. Only results that fit a single Azure
/// Table property travel this way; larger results keep the chunked, directly-awaited
/// <see cref="OrchestratorTableStore.StoreResultAsync"/> path. Written to the Results table before the
/// task's terminal status marker in the same flush, so a result is always durable before its task is
/// counted done.
/// </summary>
public record ResultWrite(string RunName, string TaskId, string ResultJson);
