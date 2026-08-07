using System.Text.Json;
using System.Text.Json.Serialization;
using Craft.Configuration;
using Craft.Services;

namespace Craft.Hosting;

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
    private readonly WorkerMetricsService _metrics;
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

    private int _ticksSinceCompact;
    private const int CompactEveryNTicks = 60; // rewrite file (apply retention) every 60 samples (~1h at 60s)
    private long _appendedSinceLoad;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public StatsHistoryService(ILogger<StatsHistoryService> logger, CraftSettings settings, WorkerMetricsService metrics)
    {
        _logger = logger;
        _settings = settings;
        _metrics = metrics;

        var dataDir = Path.Combine(AppContext.BaseDirectory, "_data");
        _dataFilePath = Path.Combine(dataDir, "stats-history.jsonl");
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
                var point = CollectSample();
                AppendPointToDisk(point);

                _ticksSinceCompact++;
                if (_ticksSinceCompact >= CompactEveryNTicks)
                {
                    CompactFile();
                    _ticksSinceCompact = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StatsHistory] Sample collection failed");
            }
        }

        // Final compaction on shutdown so retention is applied before we exit
        try { CompactFile(); } catch { }
    }

    /// <summary>Take a metrics snapshot and record a data point with delta computation.</summary>
    private StatsDataPoint CollectSample()
    {
        var snapshot = _metrics.GetSnapshot();
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

            // Memory — current state
            HeapMB = snapshot.Memory.HeapMB,
            RssMB = snapshot.Memory.RssMB,
            CommittedMB = snapshot.Memory.CommittedMB,
            ContainerLimitMB = snapshot.Memory.ContainerLimitMB,
            ContainerUsedMB = snapshot.Memory.ContainerUsedMB,
            OtherRssMB = snapshot.Memory.OtherRssMB,
            GCHeapLimitMB = snapshot.Memory.GCHeapLimitMB,
            MemoryUsagePct = snapshot.Memory.UsagePct,
            GC0 = snapshot.Memory.GC0,
            GC1 = snapshot.Memory.GC1,
            GC2 = snapshot.Memory.GC2,

            // CPU — delta-computed on the metrics snapshot path
            CpuPct = snapshot.Memory.CpuPct,
            ContainerCpuPct = snapshot.Memory.ContainerCpuPct,
            OtherCpuPct = snapshot.Memory.OtherCpuPct,
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
        return point;
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

    // ── Disk persistence (JSONL append-only + periodic compaction) ──

    /// <summary>Append a single sample as one JSON line. O(1) write, no full-file rewrite.</summary>
    private void AppendPointToDisk(StatsDataPoint point)
    {
        try
        {
            var dir = Path.GetDirectoryName(_dataFilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var line = JsonSerializer.Serialize(point, s_jsonOptions);
            File.AppendAllText(_dataFilePath, line + "\n");
            Interlocked.Increment(ref _appendedSinceLoad);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StatsHistory] Failed to append sample to disk");
        }
    }

    /// <summary>
    /// Rewrite the JSONL file from the in-memory (already-pruned) buffer. This bounds
    /// disk size to the retention window and reclaims space from expired samples.
    /// Uses a temp file + atomic move so a crash mid-rewrite cannot corrupt history.
    /// </summary>
    private void CompactFile()
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

            var tmp = _dataFilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                foreach (var p in snapshot)
                {
                    sw.WriteLine(JsonSerializer.Serialize(p, s_jsonOptions));
                }
            }
            File.Move(tmp, _dataFilePath, overwrite: true);
            Interlocked.Exchange(ref _appendedSinceLoad, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StatsHistory] Failed to compact history file");
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_dataFilePath))
            {
                // Migrate legacy single-blob file if present
                var legacy = Path.ChangeExtension(_dataFilePath, ".json");
                if (File.Exists(legacy))
                {
                    var legacyJson = File.ReadAllText(legacy);
                    var legacyLoaded = JsonSerializer.Deserialize<List<StatsDataPoint>>(legacyJson, s_jsonOptions);
                    if (legacyLoaded != null && legacyLoaded.Count > 0)
                    {
                        lock (_lock)
                        {
                            _history.Clear();
                            _history.AddRange(legacyLoaded.Where(p => p.TimestampUtc >= DateTime.UtcNow.AddDays(-RetentionDays)));
                        }
                        CompactFile();
                        try { File.Delete(legacy); } catch { }
                        _logger.LogInformation("[StatsHistory] Migrated {Count} points from legacy stats-history.json", _history.Count);
                    }
                }
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            var loaded = new List<StatsDataPoint>();
            var pruned = 0;

            foreach (var raw in File.ReadLines(_dataFilePath))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                StatsDataPoint? p;
                try { p = JsonSerializer.Deserialize<StatsDataPoint>(raw, s_jsonOptions); }
                catch { continue; } // skip a torn last line from a crash
                if (p == null) continue;
                if (p.TimestampUtc < cutoff) { pruned++; continue; }
                loaded.Add(p);
            }

            lock (_lock)
            {
                _history.Clear();
                _history.AddRange(loaded);
            }

            _logger.LogInformation("[StatsHistory] Loaded {Count} data points from disk (pruned {Pruned} expired)",
                loaded.Count, pruned);

            // If we pruned anything on load, compact immediately to reclaim space.
            if (pruned > 0) CompactFile();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StatsHistory] Failed to load from disk — starting fresh");
        }
    }
}
