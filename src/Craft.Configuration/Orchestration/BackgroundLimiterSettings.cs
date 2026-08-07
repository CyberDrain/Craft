namespace Craft.Configuration;

/// <summary>
/// Background / orchestrator concurrency limiter — gates how many BG tasks run at once on top of
/// <c>Worker.BgPoolSize</c>. Bound from <c>App:BackgroundLimiter</c>. Legacy root-level keys
/// (<c>BackgroundBaseConcurrency</c>, etc.) are still overlaid in post-bind for harness compat.
/// </summary>
public class BackgroundLimiterSettings
{
    /// <summary>
    /// Starting concurrency when idle. <c>null</c> (default) → <c>clamp(ProcessorCount, 2, 4)</c>.
    /// </summary>
    public int? BaseConcurrency { get; set; }

    /// <summary>
    /// Ceiling concurrency. <c>null</c> (default) → <c>Worker.BgPoolSize</c>.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>
    /// How long the BG queue must be backed up before ramping (seconds). Default 15.
    /// </summary>
    public int ScaleUpAfterSeconds { get; set; } = 15;

    /// <summary>
    /// Busy-HTTP-worker count that throttles BG. <c>null</c> (default) → <c>HttpPoolSize/2</c>.
    /// Set to <c>0</c> to disable HTTP-pressure throttling.
    /// </summary>
    public int? HttpPressureThreshold { get; set; }

    /// <summary>
    /// How long HTTP pressure must persist before throttling (seconds). Default 10.
    /// </summary>
    public int HttpPressureAfterSeconds { get; set; } = 10;

    /// <summary>
    /// Jump straight to the ceiling when tasks queue, skipping the ramp dwell. Default false.
    /// </summary>
    public bool BurstToCeiling { get; set; }

    /// <summary>
    /// Admit this many tasks above the worker target so they can do pre-invoke work while the pool
    /// stays full. Default 0 (strict pool cap).
    /// </summary>
    public int OverSubscribe { get; set; }
}
