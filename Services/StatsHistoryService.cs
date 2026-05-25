using System.Text.Json;
using System.Text.Json.Serialization;

namespace Craft.Services;

/// <summary>
/// Background service that periodically samples worker metrics and maintains
/// a rolling history for trend visualization. Data is kept in memory and
/// flushed to disk for persistence across container restarts.
///
/// Default: 60-second sample interval, 7-day retention (~10,080 data points).
/// </summary>
public class StatsHistoryService : BackgroundService
{
    private readonly ILogger<StatsHistoryService> _logger;
    private readonly CraftSettings _settings;
    private readonly string _dataFilePath;

    // Circular buffer — newest at the end
    private readonly List<StatsDataPoint> _history = new();
    private readonly object _lock = new();

    // Delta tracking — previous cumulative values
    private long _prevHttpInvocations;
    private long _prevHttpFaults;
    private long _prevHttpBusyMs;
    private long _prevBgInvocations;
    private long _prevBgFaults;
    private long _prevBgBusyMs;
    private long _prevJobsCompleted;
    private long _prevJobsFailed;
    private bool _hasPrevious;

    private int _ticksSinceFlush;
    private const int FlushEveryNTicks = 10; // flush to disk every 10 samples

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public StatsHistoryService(ILogger<StatsHistoryService> logger, CraftSettings settings)
    {
        _logger = logger;
        _settings = settings;

        var dataDir = Path.Combine(AppContext.BaseDirectory, "_data");
        _dataFilePath = Path.Combine(dataDir, "stats-history.json");
    }

    public int SampleIntervalSeconds => _settings.StatsHistory.SampleIntervalSeconds;
    public int RetentionDays => _settings.StatsHistory.RetentionDays;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load persisted history from disk
        LoadFromDisk();

        // Wait for worker pool to be ready before starting collection
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("[StatsHistory] Started — sampling every {Interval}s, retaining {Days} days",
            SampleIntervalSeconds, RetentionDays);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SampleIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                CollectSample();

                _ticksSinceFlush++;
                if (_ticksSinceFlush >= FlushEveryNTicks)
                {
                    FlushToDisk();
                    _ticksSinceFlush = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StatsHistory] Sample collection failed");
            }
        }

        // Final flush on shutdown
        FlushToDisk();
    }

    /// <summary>Take a metrics snapshot and record a data point with delta computation.</summary>
    private void CollectSample()
    {
        var snapshot = WorkerMetricsBridge.GetSnapshot();
        var now = DateTime.UtcNow;

        var httpPool = snapshot.HttpPool;
        var bgPool = snapshot.BgPool;
        var limiter = snapshot.Limiter;
        var jobs = snapshot.Jobs;

        var point = new StatsDataPoint
        {
            TimestampUtc = now,
            UptimeSeconds = snapshot.UptimeSeconds,

            // HTTP pool — current state
            HttpBusy = httpPool.BusyCount,
            HttpPoolSize = httpPool.PoolSize,
            HttpUtilizationPct = httpPool.AvgUtilizationPct,
            HttpAvgDurationMs = httpPool.AvgDurationMs,

            // BG pool — current state
            BgBusy = bgPool.BusyCount,
            BgPoolSize = bgPool.PoolSize,
            BgUtilizationPct = bgPool.AvgUtilizationPct,
            BgAvgDurationMs = bgPool.AvgDurationMs,

            // Jobs — current state
            JobsQueued = jobs.Queued,
            JobsRunning = jobs.Running,

            // Limiter — current state
            LimiterActive = limiter.Active,
            LimiterWaiting = limiter.Waiting,
            LimiterCurrentMax = limiter.CurrentMax,
            IsHttpThrottled = limiter.IsHttpThrottled,
        };

        // Compute deltas (invocations/faults since last sample)
        if (_hasPrevious)
        {
            point.HttpInvocations = httpPool.TotalInvocations - _prevHttpInvocations;
            point.HttpFaults = httpPool.TotalFaults - _prevHttpFaults;
            point.HttpBusyMs = httpPool.TotalBusyMs - _prevHttpBusyMs;
            point.BgInvocations = bgPool.TotalInvocations - _prevBgInvocations;
            point.BgFaults = bgPool.TotalFaults - _prevBgFaults;
            point.BgBusyMs = bgPool.TotalBusyMs - _prevBgBusyMs;
            point.JobsCompleted = jobs.Completed - _prevJobsCompleted;
            point.JobsFailed = jobs.Failed - _prevJobsFailed;
        }

        // Store current cumulative values for next delta
        _prevHttpInvocations = httpPool.TotalInvocations;
        _prevHttpFaults = httpPool.TotalFaults;
        _prevHttpBusyMs = httpPool.TotalBusyMs;
        _prevBgInvocations = bgPool.TotalInvocations;
        _prevBgFaults = bgPool.TotalFaults;
        _prevBgBusyMs = bgPool.TotalBusyMs;
        _prevJobsCompleted = jobs.Completed;
        _prevJobsFailed = jobs.Failed;
        _hasPrevious = true;

        lock (_lock)
        {
            _history.Add(point);
            PruneOldEntries(now);
        }
    }

    /// <summary>Remove entries older than the retention window.</summary>
    private void PruneOldEntries(DateTime now)
    {
        var cutoff = now.AddDays(-RetentionDays);
        // Items are chronological — find first index >= cutoff
        var firstKeep = _history.FindIndex(p => p.TimestampUtc >= cutoff);
        if (firstKeep > 0)
        {
            _history.RemoveRange(0, firstKeep);
        }
    }

    /// <summary>
    /// Query the history buffer. Optionally filter by time range and downsample.
    /// </summary>
    public List<StatsDataPoint> GetHistory(DateTime? since = null, DateTime? until = null, int? maxPoints = null)
    {
        lock (_lock)
        {
            IEnumerable<StatsDataPoint> query = _history;

            if (since.HasValue)
                query = query.Where(p => p.TimestampUtc >= since.Value);
            if (until.HasValue)
                query = query.Where(p => p.TimestampUtc <= until.Value);

            var result = query.ToList();

            // Downsample if requested (take every Nth point)
            if (maxPoints.HasValue && result.Count > maxPoints.Value && maxPoints.Value > 0)
            {
                var step = (double)result.Count / maxPoints.Value;
                var downsampled = new List<StatsDataPoint>(maxPoints.Value);
                for (double i = 0; i < result.Count && downsampled.Count < maxPoints.Value; i += step)
                {
                    downsampled.Add(result[(int)i]);
                }
                // Always include the latest point
                if (downsampled.Count > 0 && downsampled[^1] != result[^1])
                {
                    downsampled[^1] = result[^1];
                }
                return downsampled;
            }

            return result;
        }
    }

    /// <summary>Get the total number of data points currently stored.</summary>
    public int GetCount()
    {
        lock (_lock)
        {
            return _history.Count;
        }
    }

    // ── Disk persistence ──

    private void FlushToDisk()
    {
        try
        {
            List<StatsDataPoint> snapshot;
            lock (_lock)
            {
                snapshot = new List<StatsDataPoint>(_history);
            }

            var dir = Path.GetDirectoryName(_dataFilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
            File.WriteAllText(_dataFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StatsHistory] Failed to flush to disk");
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_dataFilePath)) return;

            var json = File.ReadAllText(_dataFilePath);
            var loaded = JsonSerializer.Deserialize<List<StatsDataPoint>>(json, s_jsonOptions);
            if (loaded == null || loaded.Count == 0) return;

            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            var valid = loaded.Where(p => p.TimestampUtc >= cutoff).ToList();

            lock (_lock)
            {
                _history.Clear();
                _history.AddRange(valid);
            }

            _logger.LogInformation("[StatsHistory] Loaded {Count} data points from disk (pruned {Pruned} expired)",
                valid.Count, loaded.Count - valid.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StatsHistory] Failed to load from disk — starting fresh");
        }
    }
}

/// <summary>
/// A single historical data point capturing worker pool state and interval deltas.
/// </summary>
public class StatsDataPoint
{
    public DateTime TimestampUtc { get; set; }
    public long UptimeSeconds { get; set; }

    // ── HTTP pool (current state) ──
    public int HttpBusy { get; set; }
    public int HttpPoolSize { get; set; }
    public double HttpUtilizationPct { get; set; }
    public long HttpAvgDurationMs { get; set; }

    // ── HTTP pool (delta since last sample) ──
    public long HttpInvocations { get; set; }
    public long HttpFaults { get; set; }
    public long HttpBusyMs { get; set; }

    // ── BG pool (current state) ──
    public int BgBusy { get; set; }
    public int BgPoolSize { get; set; }
    public double BgUtilizationPct { get; set; }
    public long BgAvgDurationMs { get; set; }

    // ── BG pool (delta since last sample) ──
    public long BgInvocations { get; set; }
    public long BgFaults { get; set; }
    public long BgBusyMs { get; set; }

    // ── Jobs (current state + delta) ──
    public int JobsQueued { get; set; }
    public int JobsRunning { get; set; }
    public long JobsCompleted { get; set; }
    public long JobsFailed { get; set; }

    // ── Limiter (current state) ──
    public int LimiterActive { get; set; }
    public int LimiterWaiting { get; set; }
    public int LimiterCurrentMax { get; set; }
    public bool IsHttpThrottled { get; set; }
}

/// <summary>Configuration for stats history collection.</summary>
public class StatsHistorySettings
{
    /// <summary>How often to sample metrics, in seconds. Default: 60.</summary>
    public int SampleIntervalSeconds { get; set; } = 60;

    /// <summary>How many days of history to retain. Default: 7.</summary>
    public int RetentionDays { get; set; } = 7;
}
