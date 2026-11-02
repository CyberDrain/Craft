namespace Craft.Configuration;

/// <summary>
/// Scheduler settings — drives the background cron-based task system.
/// </summary>
public class SchedulerSettings
{
    /// <summary>
    /// Path to the scheduler task definitions, relative to the API directory.
    /// Examples: "Config/CIPPTimers.json", "timers.json"
    /// Must be a JSON array of SchedulerTask objects.
    /// </summary>
    public string ConfigFile { get; set; } = "SchedulerTasks.json";

    /// <summary>How often (in seconds) the scheduler checks for due tasks.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// When true, applies the configured timezone to ALL scheduler tasks,
    /// regardless of individual TZOffset settings. When false (default),
    /// only tasks with TZOffset=true use the configured timezone.
    /// </summary>
    public bool ApplyTZOffset { get; set; }

    /// <summary>
    /// IANA or Windows timezone ID for timezone-aware cron evaluation.
    /// Overridable via env var App__Scheduler__Timezone (or CraftTZ at startup).
    /// When empty, all cron evaluation uses UTC.
    /// Examples: "America/New_York", "Europe/London", "Eastern Standard Time"
    /// </summary>
    public string Timezone { get; set; } = "";

    /// <summary>
    /// Table that records each timer's state so the hosted app can show it: one row per timer
    /// (PartitionKey "Timer", RowKey = task Id) with Command, Cron, LastFired, NextFire (jitter included),
    /// JitterMinutes, JitterSeconds and Timezone. Rewritten at startup, after each firing and on a timezone
    /// change; rows for timers no longer in the file are removed at startup. Empty (default) = not recorded.
    /// </summary>
    public string StateTable { get; set; } = "";
}
