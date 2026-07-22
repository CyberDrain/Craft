using System.Text.Json;
using Cronos;

namespace Craft.Services;

/// <summary>
/// Static bridge so PowerShell can query/set the scheduler timezone without DI.
/// PS usage: [Craft.Services.SchedulerBridge]::SetTimezone("America/New_York")
///           [Craft.Services.SchedulerBridge]::GetTimezone()
/// </summary>
public static class SchedulerBridge
{
    private static SchedulerService? s_service;

    public static void Initialize(SchedulerService service) => s_service = service;

    /// <summary>
    /// Validate and apply a new timezone. Throws if the ID is invalid.
    /// All tasks with TZOffset=true will use the new timezone on the next evaluation cycle.
    /// </summary>
    public static void SetTimezone(string timezoneId) =>
        (s_service ?? throw new InvalidOperationException("SchedulerService not initialized"))
            .SetTimezone(timezoneId);

    /// <summary>Returns the current timezone ID, or empty string if UTC.</summary>
    public static string GetTimezone() => s_service?.GetTimezone() ?? "";
}

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

public class SchedulerService : BackgroundService
{
    private readonly ILogger<SchedulerService> _logger;
    private readonly PowerShellRunnerService _psRunner;
    private readonly BackgroundTaskLimiter _limiter;
    private readonly OrchestratorService _orchestrator;
    private readonly JobManager _jobManager;
    private readonly CraftSettings _settings;
    private readonly PowerShellWorkerPool _pool;
    private readonly StorageHealthMonitor _storageHealth;
    private List<SchedulerTask> _tasks = [];
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new();
    private TimeZoneInfo _configuredTz = TimeZoneInfo.Utc;

    /// <summary>Expose loaded tasks for the API layer.</summary>
    public IReadOnlyList<SchedulerTask> Tasks => _tasks;

    public SchedulerService(
        ILogger<SchedulerService> logger,
        PowerShellRunnerService psRunner,
        BackgroundTaskLimiter limiter,
        OrchestratorService orchestrator,
        JobManager jobManager,
        CraftSettings settings,
        PowerShellWorkerPool pool,
        StorageHealthMonitor storageHealth)
    {
        _logger = logger;
        _psRunner = psRunner;
        _limiter = limiter;
        _orchestrator = orchestrator;
        _jobManager = jobManager;
        _settings = settings;
        _pool = pool;
        _storageHealth = storageHealth;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Scheduler] Waiting for worker pool to be ready");
        try
        {
            await Task.Run(() => _pool.WaitForBgReady(Timeout.InfiniteTimeSpan), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        _logger.LogInformation("[Scheduler] Worker pool ready — service starting");

        LoadConfig();
        ResolveTimezone();

        // Seed all tasks with "now" so we never catch up on missed runs from before startup.
        // Only cron ticks that occur AFTER this moment will fire.
        var startup = DateTimeOffset.UtcNow;
        foreach (var task in _tasks)
        {
            _lastRun[task.Id] = startup;
        }

        // Wait for the storage backend to accept connections before the first orchestrator store access.
        // Avoids a startup error when storage becomes reachable a moment after the app.
        await _storageHealth.WaitUntilReadyAsync(TimeSpan.FromSeconds(60), stoppingToken);

        // Resume any orchestrator runs that were interrupted by a previous crash
        try
        {
            await _orchestrator.ResumeInterruptedRunsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scheduler] Failed to resume interrupted orchestrator runs");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var task in _tasks.OrderBy(t => t.Priority))
            {
                try
                {
                    var cronExpression = CronExpression.Parse(task.Cron, CronFormat.IncludeSeconds);
                    var lastRun = _lastRun.GetValueOrDefault(task.Id, DateTimeOffset.MinValue);
                    var tz = (task.TZOffset || _settings.Scheduler.ApplyTZOffset) ? _configuredTz : TimeZoneInfo.Utc;
                    var nextOccurrence = cronExpression.GetNextOccurrence(lastRun, tz);

                    if (nextOccurrence.HasValue && nextOccurrence.Value <= now)
                    {
                        _logger.LogInformation("[Scheduler] Firing: {Command}", task.Command);
                        _lastRun[task.Id] = now;

                        if (task.IsOrchestrator)
                        {
                            // Fan-out/fan-in: planner = Command, task script derived from Command
                            // Convention: strip "Start-" prefix → "Invoke-{rest}Task"
                            var plannerFunc = FindScript(task.Command);
                            var baseName = task.Command.StartsWith("Start-", StringComparison.OrdinalIgnoreCase)
                                ? task.Command[6..]
                                : task.Command;
                            var taskScriptName = $"Invoke-{baseName}Task";
                            var taskFunc = FindScript(taskScriptName);

                            if (plannerFunc != null && taskFunc != null)
                            {
                                _ = _orchestrator.StartOrResumeRun(task.Command, plannerFunc, taskFunc, task.Priority, stoppingToken);
                                _logger.LogInformation("[Scheduler] Orchestrator started: {Command} P{Priority}", task.Command, task.Priority);
                            }
                            else
                            {
                                _logger.LogWarning("[Scheduler] Orchestrator scripts not deployed: {Command} planner={Planner} task={Task}",
                                    task.Command, task.Command, taskScriptName);
                            }
                        }
                        else
                        {
                            // Simple scheduled script — dispatch through JobManager for priority ordering
                            var scriptFunc = FindScript(task.Command);
                            if (scriptFunc != null)
                            {
                                var capturedFunc = scriptFunc;
                                var capturedParams = task.Parameters;
                                _jobManager.Enqueue(
                                    name: task.Command,
                                    priority: task.Priority,
                                    work: (ct) => _psRunner.ExecuteScript(capturedFunc, capturedParams)
                                );
                                _logger.LogInformation("[Scheduler] Enqueued: {Command} P{Priority}", task.Command, task.Priority);
                            }
                            else
                            {
                                _logger.LogWarning("[Scheduler] Script not deployed: {Command}", task.Command);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Scheduler] Error: {Command}", task.Command);
                }
            }

            // Check at configured interval
            await Task.Delay(TimeSpan.FromSeconds(_settings.Scheduler.CheckIntervalSeconds), stoppingToken);
        }
    }

    private void LoadConfig()
    {
        // Scheduler config file path is relative to the API base directory.
        // Configurable via App:Scheduler:ConfigFile (e.g. "Config/CIPPTimers.json" or "timers.json").
        var configFile = _settings.Scheduler.ConfigFile;
        var path = Path.Combine(AppContext.BaseDirectory, "API", configFile);

        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("[Scheduler] Config not found at {Path}", path);
                return;
            }

            var json = File.ReadAllText(path);
            _tasks = JsonSerializer.Deserialize<List<SchedulerTask>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
            _logger.LogInformation("[Scheduler] Loaded {Count} tasks from {Path}", _tasks.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scheduler] Failed to load config from {Path}", path);
        }
    }

    /// <summary>
    /// Resolve the configured timezone from env:CraftTZ (priority) or App:Scheduler:Timezone.
    /// Falls back to UTC if unset or invalid.
    /// </summary>
    private void ResolveTimezone()
    {
        var tzId = Environment.GetEnvironmentVariable("CraftTZ");
        if (string.IsNullOrWhiteSpace(tzId))
        {
            tzId = _settings.Scheduler.Timezone;
        }

        if (string.IsNullOrWhiteSpace(tzId))
        {
            _configuredTz = TimeZoneInfo.Utc;
            _logger.LogInformation("[Scheduler] No timezone configured, using UTC");
            return;
        }

        if (TryFindTimezone(tzId, out var tz))
        {
            _configuredTz = tz;
            _logger.LogInformation("[Scheduler] Using timezone: {TZ} (UTC{Offset})",
                tz.Id, tz.BaseUtcOffset.ToString(@"hh\:mm"));
        }
        else
        {
            _configuredTz = TimeZoneInfo.Utc;
            _logger.LogError("[Scheduler] Invalid timezone '{TZ}', falling back to UTC", tzId);
        }
    }

    /// <summary>
    /// Validate and apply a new timezone at runtime.
    /// Reseeds _lastRun for TZOffset tasks to prevent spurious fires after the switch.
    /// Callable from PowerShell via [Craft.Services.SchedulerBridge]::SetTimezone("America/New_York")
    /// </summary>
    public void SetTimezone(string timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId))
            throw new ArgumentException("Timezone ID cannot be empty.", nameof(timezoneId));

        if (!TryFindTimezone(timezoneId, out var tz))
            throw new ArgumentException($"Unknown timezone: '{timezoneId}'", nameof(timezoneId));

        _configuredTz = tz;
        _logger.LogInformation("[Scheduler] Timezone changed to: {TZ} (UTC{Offset})",
            tz.Id, tz.BaseUtcOffset.ToString(@"hh\:mm"));

        // Reseed affected tasks to "now" so the next evaluation uses the new timezone
        // without double-firing or skipping.
        var now = DateTimeOffset.UtcNow;
        foreach (var task in _tasks.Where(t => t.TZOffset || _settings.Scheduler.ApplyTZOffset))
        {
            _lastRun[task.Id] = now;
        }
    }

    /// <summary>Returns the current timezone ID, or empty string if UTC.</summary>
    public string GetTimezone() =>
        _configuredTz.Equals(TimeZoneInfo.Utc) ? "" : _configuredTz.Id;

    /// <summary>
    /// Try to find a TimeZoneInfo by IANA ID first, then Windows ID.
    /// Cronos + .NET 6+ on Linux use IANA; Windows uses Windows IDs.
    /// </summary>
    private static bool TryFindTimezone(string id, out TimeZoneInfo tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
            return false;
        }
    }

    private string? FindScript(string command) => _psRunner.FindScript(command);
}
