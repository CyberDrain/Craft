namespace Craft.Orchestration;

/// <summary>
/// Mirrors a single entry from CIPPTimers.json.
/// Type is inferred from the Command name: "*Orchestrator*" → fan-out/fan-in,
/// everything else → simple scheduled script.
/// </summary>
public class SchedulerTask
{
    public string Id { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Cron { get; set; } = "0 */15 * * * *";
    public int Priority { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
    public bool RunOnProcessor { get; set; }
    public bool IsSystem { get; set; }
    public string? PreferredProcessor { get; set; }

    /// <summary>
    /// When true, cron evaluation uses the configured timezone (env:CraftTZ / App:Scheduler:Timezone)
    /// instead of UTC. This lets operators write cron expressions in local time while Cronos
    /// handles DST transitions automatically.
    /// </summary>
    public bool TZOffset { get; set; }

    /// <summary>
    /// Explicit override for orchestrator detection. When set in CIPPTimers.json,
    /// this timer uses the StartOrResumeRun planner+task pattern.
    /// When null/unset, defaults to false (simple enqueued script).
    /// </summary>
    public bool? IsOrchestratorOverride { get; set; }

    /// <summary>True only when explicitly flagged via IsOrchestratorOverride.</summary>
    public bool IsOrchestrator => IsOrchestratorOverride ?? false;
}
