using System.Text.Json.Serialization;

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
    /// Latest this timer may fire after its cron slot, in minutes. Each instance waits the same fixed
    /// share of every timer's window (derived from its site name), so apps on a shared plan spread out,
    /// and a timer whose jitter is at least that of the timer it follows never fires closer to it.
    /// Kept below the shortest gap between the cron's slots. 0 (the default) fires on the slot.
    /// </summary>
    public int JitterMinutes { get; set; }

    /// <summary>This instance's delay after each slot, resolved by the scheduler from <see cref="JitterMinutes"/>.</summary>
    [JsonIgnore]
    public TimeSpan JitterOffset { get; set; }

    /// <summary>
    /// Explicit override for orchestrator detection. When set in CIPPTimers.json,
    /// this timer uses the StartOrResumeRun planner+task pattern.
    /// When null/unset, defaults to false (simple enqueued script).
    /// </summary>
    public bool? IsOrchestratorOverride { get; set; }

    /// <summary>True only when explicitly flagged via IsOrchestratorOverride.</summary>
    public bool IsOrchestrator => IsOrchestratorOverride ?? false;
}
