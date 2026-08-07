namespace Craft.Configuration;

/// <summary>
/// Container health monitoring — tracks restart count on persistent storage (/home)
/// to detect crash loops and force Azure to provision a new worker instance.
/// </summary>
public class ContainerHealthSettings
{
    /// <summary>
    /// Maximum consecutive restarts of the same instance within the time window
    /// before blocking Kestrel startup. Azure's health probe will then time out,
    /// forcing the platform to reallocate to a new worker.
    /// Set to 0 to disable crash-loop detection.
    /// </summary>
    public int MaxRestarts { get; set; } = 3;

    /// <summary>
    /// Time window in minutes. Only restarts within this window are counted.
    /// Restarts outside the window reset the counter.
    /// </summary>
    public int WindowMinutes { get; set; } = 30;

    /// <summary>
    /// Directory for the restart tracker file. Defaults to the app user's home on
    /// Linux (e.g. /home/app for the non-root container). Leave empty for that
    /// default; set an explicit path to override (e.g. a persistent Azure Files
    /// mount), or set MaxRestarts to 0 to disable.
    /// </summary>
    public string TrackerDirectory { get; set; } = "";
}
