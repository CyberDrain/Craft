namespace Craft.Orchestration;

/// <summary>
/// A run's identity and parentage, read from the run row alone — no task rows.
///
/// Exists so startup can rebuild parent/child run links from one partition scan instead of calling
/// <c>GetRunAsync</c> per run, which loads every task of every run.
/// </summary>
/// <param name="Name">Run name (the run row's RowKey).</param>
/// <param name="Status">Running | Completed | CompletedWithErrors | Failed.</param>
/// <param name="ParentRunName">Parent run, when this run was spawned by a task of another run.</param>
public record OrchestratorRunSummary(string Name, string Status, string? ParentRunName);
