namespace Craft.Storage;

/// <summary>
/// What one <see cref="OrchestratorTableStore.CleanupOldRunsAsync"/> pass removed, so the caller can
/// log it and take the follow-up that is not the store's business (an abandoned run's queue rows).
/// </summary>
/// <param name="RunsExamined">Run rows scanned.</param>
/// <param name="ExpiredRuns">Runs that had finished and were past retention.</param>
/// <param name="AbandonedRuns">Runs that had not finished, were not active, and had not been written to within retention.</param>
/// <param name="OrphanPartitionsRemoved">Tasks/Results partitions with no Run row whose newest row was past retention.</param>
public sealed record OrchestratorCleanupResult(
    int RunsExamined,
    IReadOnlyList<string> ExpiredRuns,
    IReadOnlyList<string> AbandonedRuns,
    int OrphanPartitionsRemoved);
