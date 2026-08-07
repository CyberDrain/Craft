namespace Craft.Configuration;

/// <summary>Configuration for stats history collection.</summary>
public class StatsHistorySettings
{
    /// <summary>How often to sample metrics, in seconds. Default: 60.</summary>
    public int SampleIntervalSeconds { get; set; } = 60;

    /// <summary>How many days of history to retain. Default: 7.</summary>
    public int RetentionDays { get; set; } = 7;
}
