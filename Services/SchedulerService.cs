using System.Text.Json;
using Cronos;

namespace Craft.Services;

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
    private List<SchedulerTask> _tasks = [];
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new();

    /// <summary>Expose loaded tasks for the API layer.</summary>
    public IReadOnlyList<SchedulerTask> Tasks => _tasks;

    public SchedulerService(
        ILogger<SchedulerService> logger,
        PowerShellRunnerService psRunner,
        BackgroundTaskLimiter limiter,
        OrchestratorService orchestrator,
        JobManager jobManager,
        CraftSettings settings)
    {
        _logger = logger;
        _psRunner = psRunner;
        _limiter = limiter;
        _orchestrator = orchestrator;
        _jobManager = jobManager;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Scheduler] Service starting");
        LoadConfig();

        // Seed all tasks with "now" so we never catch up on missed runs from before startup.
        // Only cron ticks that occur AFTER this moment will fire.
        var startup = DateTimeOffset.UtcNow;
        foreach (var task in _tasks)
        {
            _lastRun[task.Id] = startup;
        }

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
                    var nextOccurrence = cronExpression.GetNextOccurrence(lastRun, TimeZoneInfo.Utc);

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

    private string? FindScript(string command) => _psRunner.FindScript(command);
}
