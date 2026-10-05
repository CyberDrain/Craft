namespace Craft.Configuration;

/// <summary>
/// Orchestrator settings — fan-out/fan-in task execution, with all state in storage.
/// </summary>
public class OrchestratorSettings
{
    /// <summary>
    /// Prefix for the orchestrator's Azure Tables.
    /// Tables created: {Prefix}Work, {Prefix}Ready, {Prefix}Names, {Prefix}Finished, {Prefix}TaskResults.
    /// </summary>
    public string TablePrefix { get; set; } = "Orchestrator";

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

    /// <summary>Maximum number of times a task can be interrupted before being marked Failed, and how many
    /// times a run's PostExecution is attempted.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// How long a finished run's rows outlive it (hours, default 48). Craft needs them only while the run
    /// is live; they stay this long for operators reading recent history.
    /// </summary>
    public int RetentionHours { get; set; } = 48;

    /// <summary>
    /// How often the retention sweep runs after the one at startup (hours, default 4). 0 disables the
    /// periodic sweep; the startup pass still runs.
    /// </summary>
    public int CleanupIntervalHours { get; set; } = 4;

    /// <summary>
    /// How often each active run's status line is considered for logging (seconds, default 60). A line is
    /// written when the run's counts change, or every ten minutes when they do not. Minimum 1s.
    /// </summary>
    public int StatusTimerIntervalSeconds { get; set; } = 60;
}
