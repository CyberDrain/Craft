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
    /// How long a task will wait for its durable "Running" marker before giving up (seconds, default 90).
    ///
    /// This wait sits between the JobManager dispatching a task and that task checking out a worker, so
    /// an unbounded one is a whole-host outage: production wedged for 101 and 75 minutes with all 8
    /// limiter slots held by tasks blocked here, every BG worker idle, and the heap at 24% of its cap.
    /// On timeout the task is deferred, NOT failed — the marker never landed, so storage still has it
    /// Pending and it is retried. Must exceed <see cref="StatusFlushTimeoutSeconds"/>, since a waiter
    /// may need the in-flight flush to finish plus one more.
    /// </summary>
    public int RunningBarrierTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Ceiling on one flush of coalesced status writes (seconds, default 30). The drain loop is a single
    /// loop, so an unbounded storage call inside a flush stops every status write and every barrier
    /// waiter in the process. Writes that do not complete are put back and retried on the next flush.
    /// </summary>
    public int StatusFlushTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How many per-run status writes may be in flight within one flush (default 8).
    ///
    /// Writes are grouped by run because a batch must share a partition key. The workload that broke
    /// this was ~600 runs of ONE task each (one per tenant), which turned a "batch" into hundreds of
    /// sequential round-trips inside a single flush. 1 restores the old sequential behaviour.
    /// </summary>
    public int StatusFlushConcurrency { get; set; } = 8;

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

    /// <summary>
    /// How long a run's rows outlive it (hours, default 48). A run that finished — or that nothing is
    /// driving and that last wrote to storage — longer ago than this is removed from all three tables,
    /// together with any Tasks/Results partition whose Run row is already gone. Craft itself needs the
    /// rows only while a run is live; they stay this long for operators reading recent history.
    /// </summary>
    public int RetentionHours { get; set; } = 48;

    /// <summary>
    /// How often the retention sweep runs after the one at startup (hours, default 4). 0 disables the
    /// periodic sweep; the startup pass, which follows crash recovery, still runs.
    /// </summary>
    public int CleanupIntervalHours { get; set; } = 4;
}
