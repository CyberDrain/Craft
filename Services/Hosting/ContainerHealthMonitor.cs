using System.Text.Json;
using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Tracks container restart count on persistent Azure Files storage (/home).
/// On startup, if the same WEBSITE_INSTANCE_ID has crashed repeatedly within a
/// time window, the monitor signals that Kestrel should NOT start — causing
/// Azure's warmup probe to time out and forcing a worker reallocation.
///
/// Flow:
///   1. App starts → record restart attempt to {home}/restart-tracker.json
///   2. If restart count exceeds threshold → ShouldBlockStartup = true, Kestrel never starts,
///      Azure health check fails, platform provisions a new worker
///   3. If app starts successfully (pool ready) → clear the restart counter
/// </summary>
public class ContainerHealthMonitor
{
    // Cached: JsonSerializerOptions builds and caches serialization metadata on first use, so a new
    // instance per call throws that work away every time.
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    private readonly ILogger _logger;
    private readonly string _trackerPath;
    private readonly int _maxRestarts;
    private readonly TimeSpan _window;

    public bool ShouldBlockStartup { get; private set; }

    public ContainerHealthMonitor(ILogger<ContainerHealthMonitor> logger, ContainerHealthSettings settings)
    {
        _logger = logger;
        _maxRestarts = settings.MaxRestarts;
        _window = TimeSpan.FromMinutes(settings.WindowMinutes);

        // Resolve tracker file path:
        // - Azure App Service with WEBSITES_ENABLE_APP_SERVICE_STORAGE: /home is persistent Azure Files
        // - Local dev / non-Azure: skip entirely
        var homePath = settings.TrackerDirectory;
        if (string.IsNullOrEmpty(homePath))
        {
            // Default to the app user's home on Linux (writable by the non-root
            // container); skip on Windows/dev. Override via TrackerDirectory.
            homePath = OperatingSystem.IsLinux() ? RuntimePaths.Home : "";
        }

        _trackerPath = string.IsNullOrEmpty(homePath) ? "" : Path.Combine(homePath, "restart-tracker.json");
    }

    /// <summary>
    /// Record a startup attempt. Call this BEFORE Kestrel starts.
    /// Reads the existing tracker, increments count if same instance within window,
    /// and decides whether to block startup.
    /// </summary>
    public void RecordStartupAttempt()
    {
        if (string.IsNullOrEmpty(_trackerPath))
        {
            _logger.LogDebug("[Health] Container health monitor disabled — no persistent storage path configured");
            return;
        }

        // Warn if WEBSITES_ENABLE_APP_SERVICE_STORAGE isn't explicitly true — without it,
        // /home is ephemeral in custom containers and the tracker file is lost on every restart.
        var storageEnabled = Environment.GetEnvironmentVariable("WEBSITES_ENABLE_APP_SERVICE_STORAGE");
        if (!string.Equals(storageEnabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "[Health] WEBSITES_ENABLE_APP_SERVICE_STORAGE is not set to 'true' (current: '{Value}'). " +
                "The /home directory is NOT persistent in custom containers without this setting — " +
                "crash-loop detection will not work. Set this App Setting to 'true' to enable persistent storage.",
                storageEnabled ?? "(not set)");
            return;
        }

        var instanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "unknown";
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "unknown";
        var imageTag = Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "unknown";
        var now = DateTimeOffset.UtcNow;

        RestartTracker tracker;
        try
        {
            tracker = ReadTracker();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Health] Failed to read restart tracker — starting fresh");
            tracker = new RestartTracker();
        }

        // Check if this is the same instance crashing within the time window
        var sameInstance = string.Equals(tracker.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(tracker.SiteName, siteName, StringComparison.OrdinalIgnoreCase);
        var withinWindow = tracker.LastAttemptUtc.HasValue
            && (now - tracker.LastAttemptUtc.Value) < _window;

        if (sameInstance && withinWindow)
        {
            tracker.RestartCount++;
        }
        else
        {
            // New instance or outside time window — reset
            tracker = new RestartTracker
            {
                InstanceId = instanceId,
                SiteName = siteName,
                ImageTag = imageTag,
                RestartCount = 1,
                FirstAttemptUtc = now
            };
        }

        tracker.LastAttemptUtc = now;
        tracker.ImageTag = imageTag;

        _logger.LogInformation(
            "[Health] Container startup attempt #{Count} — Instance={Instance}, Site={Site}, Image={Image}, Window={Window}min",
            tracker.RestartCount, instanceId, siteName, imageTag, _window.TotalMinutes);

        if (tracker.RestartCount > _maxRestarts)
        {
            _logger.LogCritical(
                "[Health] BLOCKING STARTUP — Instance {Instance} has crashed {Count} times in {Window} minutes " +
                "(threshold: {Max}). Refusing to start Kestrel so Azure health check fails and provisions a new worker. " +
                "First failure: {First}, Latest: {Latest}",
                instanceId, tracker.RestartCount, _window.TotalMinutes, _maxRestarts,
                tracker.FirstAttemptUtc, tracker.LastAttemptUtc);

            ShouldBlockStartup = true;
        }

        try
        {
            WriteTracker(tracker);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Health] Failed to write restart tracker");
        }
    }

    /// <summary>
    /// Clear the restart counter. Call this once the app has started successfully
    /// (pool is ready, serving traffic). This prevents a stale counter from blocking
    /// the next legitimate restart.
    /// </summary>
    public void ClearRestartCounter()
    {
        if (string.IsNullOrEmpty(_trackerPath))
            return;

        try
        {
            if (File.Exists(_trackerPath))
            {
                File.Delete(_trackerPath);
                _logger.LogInformation("[Health] Restart counter cleared — app started successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Health] Failed to clear restart tracker");
        }
    }

    private RestartTracker ReadTracker()
    {
        if (!File.Exists(_trackerPath))
            return new RestartTracker();

        var json = File.ReadAllText(_trackerPath);
        return JsonSerializer.Deserialize<RestartTracker>(json) ?? new RestartTracker();
    }

    private void WriteTracker(RestartTracker tracker)
    {
        var dir = Path.GetDirectoryName(_trackerPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(tracker, s_indented);
        File.WriteAllText(_trackerPath, json);
    }

    private sealed class RestartTracker
    {
        public string InstanceId { get; set; } = "";
        public string SiteName { get; set; } = "";
        public string ImageTag { get; set; } = "";
        public int RestartCount { get; set; }
        public DateTimeOffset? FirstAttemptUtc { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
    }
}
