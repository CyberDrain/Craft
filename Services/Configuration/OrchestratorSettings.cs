namespace Craft.Configuration;

/// <summary>
/// Orchestrator settings — fan-out/fan-in task execution with crash recovery.
/// </summary>
public class OrchestratorSettings
{
    /// <summary>
    /// Prefix for the three Azure Tables used by the orchestrator.
    /// Tables created: {Prefix}Runs, {Prefix}Tasks, {Prefix}Results.
    /// </summary>
    public string TablePrefix { get; set; } = "Orchestrator";

    /// <summary>
    /// Batch and coalesce per-task/run status writes through OrchestratorStatusWriter instead of writing each
    /// individually. Removes the per-task Azure Table write from the fan-out critical path (the throughput
    /// ceiling — see docs/orch-analysis.md). Default true. Results are never batched (their chunking path is
    /// untouched). Set false to fall back to the original per-task writes (for A/B).
    /// </summary>
    public bool BatchStatusWrites { get; set; } = true;

    /// <summary>
    /// When batching status writes, write the pre-invoke "Running" marker under a synchronous barrier so it
    /// is durable BEFORE the task invokes (batched with other concurrently-starting tasks). Preserves the
    /// AttemptCount/MaxRetries poison-task guarantee. Default true. False = eventual (faster, weaker: the
    /// marker rides the periodic flush, so a host crash within the flush window may not advance AttemptCount).
    /// </summary>
    public bool DurableRunningBarrier { get; set; } = true;

    /// <summary>How often (ms) the status writer flushes coalesced writes. Also the barrier latency ceiling.
    /// Default 25.</summary>
    public int StatusFlushIntervalMs { get; set; } = 25;

    /// <summary>
    /// PowerShell function used to execute individual orchestrator tasks.
    /// Receives a hashtable with task parameters.
    /// </summary>
    public string GenericTaskFunction { get; set; } = "Invoke-CraftTask";

    /// <summary>
    /// PowerShell function used to process queued commands.
    /// Receives Cmdlet + ParametersJson.
    /// </summary>
    public string QueueTaskFunction { get; set; } = "Invoke-CraftQueueTask";

    /// <summary>
    /// PowerShell function called after all tasks in a run complete.
    /// Receives the run name and result data.
    /// </summary>
    public string PostExecFunction { get; set; } = "Invoke-CraftPostExecution";

    /// <summary>Maximum number of times a task can be interrupted before being marked Failed.</summary>
    public int MaxRetries { get; set; } = 3;
}
