using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;

namespace Craft.Hosting;

/// <summary>
/// Domain-owned worker pool metrics. PowerShell reaches the same data via
/// <c>WorkerMetricsBridge</c>; C# callers inject this service instead of the static bridge.
/// </summary>
public sealed class WorkerMetricsService
{

    private readonly PowerShellWorkerPool _pool;
    private readonly BackgroundTaskLimiter _limiter;
    private readonly JobManager _jobManager;
    private readonly ILogger<WorkerMetricsService> _logger;

    // Per-worker tracking: workerId → stats
    private readonly ConcurrentDictionary<int, WorkerStats> _workerStats = new();
    private readonly DateTime _startTimeUtc = DateTime.UtcNow;
    private long _globalInvocations;

    // Retired-worker totals: when a worker is recycled, its accumulated counters are
    // moved here so pool aggregates remain monotonic across recycles. Without these,
    // pool.TotalInvocations would drop on recycle and StatsHistoryService deltas
    // (BgInvocations, BgBusyMs) would go negative.
    private long _retiredHttpInvocations;
    private long _retiredHttpBusyMs;
    private long _retiredHttpFaults;
    private long _retiredBgInvocations;
    private long _retiredBgBusyMs;
    private long _retiredBgFaults;

    public WorkerMetricsService(
        ILogger<WorkerMetricsService> logger,
        PowerShellWorkerPool pool,
        BackgroundTaskLimiter limiter,
        JobManager jobManager)
    {
        _logger = logger;
        _pool = pool;
        _limiter = limiter;
        _jobManager = jobManager;
    }

    // ── Called by PowerShellWorkerPool/RunnerService at checkout/reclaim ──

    /// <summary>Pre-register a worker so it appears in snapshots even before first use.</summary>
    public void RegisterWorker(int workerId, bool isHttp)
    {
        var stats = _workerStats.GetOrAdd(workerId, _ => new WorkerStats { WorkerId = workerId });
        stats.IsHttp = isHttp;
    }

    /// <summary>Remove a worker's stats when it is recycled/replaced.</summary>
    public void DeregisterWorker(int workerId)
    {
        // Accumulate the retiring worker's totals into the per-pool "retired" buckets
        // so pool-level sums (and delta-based history) stay monotonic across recycles.
        if (_workerStats.TryRemove(workerId, out var stats))
        {
            var inv = Interlocked.Read(ref stats._totalInvocations);
            var busy = Interlocked.Read(ref stats._totalBusyMs);
            var faults = Interlocked.Read(ref stats._totalFaults);
            if (stats.IsHttp)
            {
                Interlocked.Add(ref _retiredHttpInvocations, inv);
                Interlocked.Add(ref _retiredHttpBusyMs, busy);
                Interlocked.Add(ref _retiredHttpFaults, faults);
            }
            else
            {
                Interlocked.Add(ref _retiredBgInvocations, inv);
                Interlocked.Add(ref _retiredBgBusyMs, busy);
                Interlocked.Add(ref _retiredBgFaults, faults);
            }
        }
    }

    /// <summary>Record that a worker was checked out (started processing).</summary>
    public void RecordCheckout(int workerId, bool isHttp)
    {
        var stats = _workerStats.GetOrAdd(workerId, _ => new WorkerStats { WorkerId = workerId });
        stats.IsHttp = isHttp;
        stats.LastCheckoutUtc = DateTime.UtcNow;
        stats.IsBusy = true;
        // Capture per-thread allocation baseline so RecordReclaim can compute the delta
        // attributable to this invocation. Sub-100ns intrinsic, no syscall.
        stats.CheckoutAllocBytes = GC.GetAllocatedBytesForCurrentThread();
        Interlocked.Increment(ref stats._totalInvocations);
    }

    /// <summary>Record that a worker was reclaimed (finished processing).</summary>
    public void RecordReclaim(int workerId, bool faulted, long elapsedMs)
    {
        if (!_workerStats.TryGetValue(workerId, out var stats)) return;

        stats.IsBusy = false;
        stats.LastReclaimUtc = DateTime.UtcNow;
        stats.LastDurationMs = elapsedMs;

        Interlocked.Add(ref stats._totalBusyMs, elapsedMs);
        if (faulted) Interlocked.Increment(ref stats._totalFaults);

        // Allocation delta attributable to this invocation.
        // Same-thread comparison; if PowerShell hopped threads (rare for sync runspaces)
        // the delta could be slightly off but still useful as a directional signal.
        var allocEnd = GC.GetAllocatedBytesForCurrentThread();
        var allocDelta = allocEnd - stats.CheckoutAllocBytes;
        if (allocDelta > 0)
        {
            Interlocked.Add(ref stats._totalAllocBytes, allocDelta);
            stats.LastAllocBytes = allocDelta;
        }

        // Track min/max/recent durations
        UpdateDurationStats(stats, elapsedMs);

        // Every 100 global invocations, attempt a memory trim (2-min cooldown)
        var count = Interlocked.Increment(ref _globalInvocations);
        if (count % 100 == 0)
        {
            var reclaimed = TrimMemory();
            if (reclaimed >= 0)
                _logger.LogInformation("[System] Memory trim at invocation {Count}: reclaimed ~{MB}MB {Memory}",
                    count, reclaimed, BackgroundTaskLimiter.GetMemorySnapshot());
        }
    }

    /// <summary>Record the function name being executed on a worker.</summary>
    public void RecordFunction(int workerId, string functionName)
    {
        if (!_workerStats.TryGetValue(workerId, out var stats)) return;
        stats.CurrentFunction = functionName;
    }

    // ── Public query methods ──

    /// <summary>Get a full snapshot of all worker metrics.</summary>
    public WorkerMetricsSnapshot GetSnapshot()
    {
        var snapshot = new WorkerMetricsSnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            UptimeSeconds = (long)(DateTime.UtcNow - _startTimeUtc).TotalSeconds,
        };

        var httpWorkers = new List<WorkerDetail>();
        var bgWorkers = new List<WorkerDetail>();

        foreach (var (workerId, stats) in _workerStats)
        {
            var detail = BuildWorkerDetail(stats);
            if (stats.IsHttp)
                httpWorkers.Add(detail);
            else
                bgWorkers.Add(detail);
        }

        snapshot.HttpPool = new PoolMetrics
        {
            PoolSize = _pool.HttpPoolSize,
            Available = _pool.HttpAvailable,
            BusyCount = _pool.HttpPoolSize - _pool.HttpAvailable,
            Workers = httpWorkers,
        };

        snapshot.BgPool = new PoolMetrics
        {
            PoolSize = _pool.BgPoolSize,
            Available = _pool.BgAvailable,
            BusyCount = _pool.BgPoolSize - _pool.BgAvailable,
            Workers = bgWorkers,
        };

        // Aggregate pool-level stats (live workers + retired buckets so totals are monotonic)
        AggregatePoolStats(snapshot.HttpPool, httpWorkers,
            Interlocked.Read(ref _retiredHttpInvocations),
            Interlocked.Read(ref _retiredHttpBusyMs),
            Interlocked.Read(ref _retiredHttpFaults));
        AggregatePoolStats(snapshot.BgPool, bgWorkers,
            Interlocked.Read(ref _retiredBgInvocations),
            Interlocked.Read(ref _retiredBgBusyMs),
            Interlocked.Read(ref _retiredBgFaults));

        snapshot.Limiter = new LimiterMetrics
        {
            BaseConcurrency = _limiter.BaseConcurrency,
            CeilingConcurrency = _limiter.CeilingConcurrency,
            CurrentMax = _limiter.CurrentMax,
            Active = _limiter.Active,
            Waiting = _limiter.Waiting,
            IsHttpThrottled = _limiter.IsHttpThrottled,
        };

        {
            var summary = _jobManager.GetSummary();
            snapshot.Jobs = new JobMetrics
            {
                Queued = summary.Queued,
                Running = summary.Running,
                Completed = summary.Completed,
                Failed = summary.Failed,
                TotalProcessed = summary.TotalProcessed,
                MaxConcurrency = summary.MaxConcurrency,
                ActiveConcurrency = summary.ActiveConcurrency,
            };
        }

        // Memory metrics — always populated (no dependency on services)
        var heapBytes = GC.GetTotalMemory(false);
        var workingSet = Environment.WorkingSet;
        var gcInfo = GC.GetGCMemoryInfo();

        // TotalAvailableMemoryBytes reflects the GC heap hard limit when one is set
        // (e.g. DOTNET_GCHeapHardLimitPercent), not the actual container memory.
        // Read the real cgroup limit so the dashboard shows container-level usage.
        var containerLimit = GetContainerMemoryLimit() ?? gcInfo.TotalAvailableMemoryBytes;
        var containerUsed = GetContainerMemoryUsage() ?? workingSet;
        var containerCpuPct = GetContainerCpuPct();
        var processCpuPct = GetCpuPct();
        // "Other" = container total minus our process. On a single-process container this
        // is mostly page cache + kernel accounting; with sidecars it is real.
        var otherRss = Math.Max(0, containerUsed - workingSet);
        var otherCpu = Math.Max(0, containerCpuPct - processCpuPct);

        snapshot.Memory = new MemoryMetrics
        {
            HeapMB = heapBytes / (1024 * 1024),
            RssMB = workingSet / (1024 * 1024),
            CommittedMB = gcInfo.TotalCommittedBytes / (1024 * 1024),
            ContainerLimitMB = containerLimit / (1024 * 1024),
            ContainerUsedMB = containerUsed / (1024 * 1024),
            ContainerFreeMB = Math.Max(0, (containerLimit - containerUsed) / (1024 * 1024)),
            OtherRssMB = otherRss / (1024 * 1024),
            GCHeapLimitMB = gcInfo.TotalAvailableMemoryBytes / (1024 * 1024),
            UsagePct = containerLimit > 0
                ? Math.Round(containerUsed * 100.0 / containerLimit, 1)
                : 0,
            GC0 = GC.CollectionCount(0),
            GC1 = GC.CollectionCount(1),
            GC2 = GC.CollectionCount(2),
            CpuPct = processCpuPct,
            ContainerCpuPct = containerCpuPct,
            OtherCpuPct = Math.Round(otherCpu, 1),
        };

        return snapshot;
    }

    private DateTime _prevCpuSampleUtc = DateTime.UtcNow;
    private TimeSpan _prevCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private double _lastCpuPct;
    private readonly object _cpuLock = new();
    private readonly int _processorCount = Environment.ProcessorCount;

    /// <summary>
    /// Compute process CPU% since the last call. Returns a value 0–100 representing
    /// usage across all cores (e.g. 50% on a 2-core machine = 1 core fully busy).
    /// Uses a minimum 500ms window to avoid divide-by-tiny-number noise.
    /// </summary>
    private double GetCpuPct()
    {
        lock (_cpuLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _prevCpuSampleUtc;

            if (elapsed.TotalMilliseconds < 500)
                return _lastCpuPct; // Too soon — return last known value

            var proc = Process.GetCurrentProcess();
            var cpuTime = proc.TotalProcessorTime;
            var cpuDelta = cpuTime - _prevCpuTime;

            _lastCpuPct = Math.Round(cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * _processorCount) * 100, 1);
            _prevCpuSampleUtc = now;
            _prevCpuTime = cpuTime;

            return _lastCpuPct;
        }
    }

    /// <summary>
    /// Read the real container cgroup memory limit from /sys/fs/cgroup.
    /// Returns null on non-Linux or if the file doesn't exist.
    /// </summary>
    private static long? GetContainerMemoryLimit()
    {
        try
        {
            // cgroup v2
            const string cgroupV2 = "/sys/fs/cgroup/memory.max";
            if (File.Exists(cgroupV2))
            {
                var text = File.ReadAllText(cgroupV2).Trim();
                if (text != "max" && long.TryParse(text, out var bytes))
                    return bytes;
            }

            // cgroup v1
            const string cgroupV1 = "/sys/fs/cgroup/memory/memory.limit_in_bytes";
            if (File.Exists(cgroupV1))
            {
                var text = File.ReadAllText(cgroupV1).Trim();
                if (long.TryParse(text, out var bytes) && bytes < long.MaxValue / 2)
                    return bytes;
            }
        }
        catch { /* not in a container or no permissions */ }

        return null;
    }

    /// <summary>
    /// Read actual container RSS (everything inside the cgroup, including page cache and
    /// any sidecar processes). Returns null on non-Linux. Single file read.
    /// </summary>
    private static long? GetContainerMemoryUsage()
    {
        try
        {
            const string cgroupV2 = "/sys/fs/cgroup/memory.current";
            if (File.Exists(cgroupV2))
            {
                var text = File.ReadAllText(cgroupV2).Trim();
                if (long.TryParse(text, out var bytes)) return bytes;
            }
            const string cgroupV1 = "/sys/fs/cgroup/memory/memory.usage_in_bytes";
            if (File.Exists(cgroupV1))
            {
                var text = File.ReadAllText(cgroupV1).Trim();
                if (long.TryParse(text, out var bytes)) return bytes;
            }
        }
        catch { }
        return null;
    }

    private long _prevContainerCpuUsec;
    private DateTime _prevContainerCpuSampleUtc = DateTime.MinValue;
    private double _lastContainerCpuPct;
    private readonly object _containerCpuLock = new();

    /// <summary>
    /// Read total CPU usage across the container cgroup. Returns 0 on non-Linux. Same
    /// 500ms minimum-window guard as GetCpuPct so consecutive calls are cheap.
    /// </summary>
    private double GetContainerCpuPct()
    {
        lock (_containerCpuLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _prevContainerCpuSampleUtc;
            if (_prevContainerCpuSampleUtc != DateTime.MinValue && elapsed.TotalMilliseconds < 500)
                return _lastContainerCpuPct;

            long? usec = null;
            try
            {
                const string cgroupV2 = "/sys/fs/cgroup/cpu.stat";
                if (File.Exists(cgroupV2))
                {
                    foreach (var line in File.ReadAllLines(cgroupV2))
                    {
                        if (line.StartsWith("usage_usec ", StringComparison.Ordinal) &&
                            long.TryParse(line.AsSpan("usage_usec ".Length), out var v))
                        {
                            usec = v;
                            break;
                        }
                    }
                }
                else
                {
                    const string cgroupV1 = "/sys/fs/cgroup/cpuacct/cpuacct.usage";
                    if (File.Exists(cgroupV1))
                    {
                        var text = File.ReadAllText(cgroupV1).Trim();
                        if (long.TryParse(text, out var ns)) usec = ns / 1000;
                    }
                }
            }
            catch { }

            if (!usec.HasValue) return 0;

            if (_prevContainerCpuSampleUtc == DateTime.MinValue)
            {
                _prevContainerCpuUsec = usec.Value;
                _prevContainerCpuSampleUtc = now;
                return 0;
            }

            var cpuDeltaMs = (usec.Value - _prevContainerCpuUsec) / 1000.0;
            _lastContainerCpuPct = Math.Round(
                cpuDeltaMs / (elapsed.TotalMilliseconds * _processorCount) * 100, 1);
            _prevContainerCpuUsec = usec.Value;
            _prevContainerCpuSampleUtc = now;
            return _lastContainerCpuPct;
        }
    }

    /// <summary>
    /// Get a detailed memory breakdown including per-generation heap sizes, LOH/POH,
    /// fragmentation, pinned objects, thread info, loaded assemblies, and native memory.
    /// Designed for diagnostics — more expensive than the basic MemoryMetrics in GetSnapshot().
    /// </summary>
    public MemoryBreakdown GetMemoryBreakdown()
    {
        var proc = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var heapBytes = GC.GetTotalMemory(false);
        var workingSet = proc.WorkingSet64;
        var containerBytes = GetContainerMemoryLimit() ?? gcInfo.TotalAvailableMemoryBytes;

        // Per-generation sizes from GenerationInfo array (indices: 0=Gen0, 1=Gen1, 2=Gen2, 3=LOH, 4=POH)
        var genInfo = gcInfo.GenerationInfo;
        var generations = new GenerationDetail[genInfo.Length];
        for (int i = 0; i < genInfo.Length; i++)
        {
            generations[i] = new GenerationDetail
            {
                Generation = i switch { 0 => "Gen0", 1 => "Gen1", 2 => "Gen2", 3 => "LOH", 4 => "POH", _ => $"Gen{i}" },
                SizeBytes = genInfo[i].SizeAfterBytes,
                SizeMB = Math.Round(genInfo[i].SizeAfterBytes / (1024.0 * 1024), 2),
                FragmentationBytes = genInfo[i].FragmentationAfterBytes,
                FragmentationMB = Math.Round(genInfo[i].FragmentationAfterBytes / (1024.0 * 1024), 2),
            };
        }

        // Thread breakdown
        var threads = proc.Threads;
        var threadStates = new Dictionary<string, int>();
        foreach (ProcessThread t in threads)
        {
            try
            {
                var state = t.ThreadState.ToString();
                threadStates[state] = threadStates.GetValueOrDefault(state) + 1;
            }
            catch { /* access denied on some threads */ }
        }

        // Loaded assemblies
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        long asmTotalSize = 0;
        var asmCategories = new Dictionary<string, int>();
        foreach (var asm in assemblies)
        {
            try
            {
                if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
                    asmTotalSize += new FileInfo(asm.Location).Length;
            }
            catch { /* skip */ }

            var name = asm.GetName().Name ?? "unknown";
            var category = name switch
            {
                _ when name.StartsWith("System.", StringComparison.Ordinal) => "System",
                _ when name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) => "ASP.NET",
                _ when name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) => "Extensions",
                _ when name.StartsWith("Microsoft.", StringComparison.Ordinal) => "Microsoft",
                _ when name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("SMA", StringComparison.Ordinal) => "PowerShell",
                "Craft" or "CIPPSharp" => "App",
                _ => "Other",
            };
            asmCategories[category] = asmCategories.GetValueOrDefault(category) + 1;
        }

        // Pool memory estimates (each runspace holds modules, function table, variable table)
        var httpPoolSize = _pool.HttpPoolSize;
        var bgPoolSize = _pool.BgPoolSize;
        var totalWorkers = httpPoolSize + bgPoolSize;

        // Native memory = RSS - committed managed
        var nativeBytes = workingSet - gcInfo.TotalCommittedBytes;
        if (nativeBytes < 0) nativeBytes = 0;

        // GC pause info
        var pauseDurations = gcInfo.PauseDurations;
        var totalPauseMs = 0.0;
        foreach (var p in pauseDurations)
            totalPauseMs += p.TotalMilliseconds;

        return new MemoryBreakdown
        {
            TimestampUtc = DateTime.UtcNow,
            UptimeSeconds = (long)(DateTime.UtcNow - _startTimeUtc).TotalSeconds,

            // Top-level sizes
            HeapBytes = heapBytes,
            HeapMB = Math.Round(heapBytes / (1024.0 * 1024), 2),
            RssBytes = workingSet,
            RssMB = Math.Round(workingSet / (1024.0 * 1024), 2),
            CommittedBytes = gcInfo.TotalCommittedBytes,
            CommittedMB = Math.Round(gcInfo.TotalCommittedBytes / (1024.0 * 1024), 2),
            NativeEstimateBytes = nativeBytes,
            NativeEstimateMB = Math.Round(nativeBytes / (1024.0 * 1024), 2),
            ContainerLimitMB = containerBytes / (1024 * 1024),
            GCHeapLimitMB = gcInfo.TotalAvailableMemoryBytes / (1024 * 1024),

            // Per-generation detail
            Generations = generations,
            TotalFragmentationBytes = gcInfo.FragmentedBytes,
            TotalFragmentationMB = Math.Round(gcInfo.FragmentedBytes / (1024.0 * 1024), 2),
            PinnedObjectsCount = gcInfo.PinnedObjectsCount,
            FinalizationPendingCount = gcInfo.FinalizationPendingCount,
            PromotedBytes = gcInfo.PromotedBytes,
            PromotedMB = Math.Round(gcInfo.PromotedBytes / (1024.0 * 1024), 2),

            // GC state
            GC0 = GC.CollectionCount(0),
            GC1 = GC.CollectionCount(1),
            GC2 = GC.CollectionCount(2),
            Compacted = gcInfo.Compacted,
            Concurrent = gcInfo.Concurrent,
            GCPauseMs = Math.Round(totalPauseMs, 2),
            MemoryLoadBytes = gcInfo.MemoryLoadBytes,
            MemoryLoadMB = Math.Round(gcInfo.MemoryLoadBytes / (1024.0 * 1024), 2),
            HighMemoryLoadThresholdBytes = gcInfo.HighMemoryLoadThresholdBytes,
            HighMemoryLoadThresholdMB = Math.Round(gcInfo.HighMemoryLoadThresholdBytes / (1024.0 * 1024), 2),

            // Threads
            ThreadCount = threads.Count,
            ThreadStates = threadStates,
            ProcessorCount = _processorCount,

            // Assemblies
            LoadedAssemblyCount = assemblies.Length,
            AssemblyDiskSizeBytes = asmTotalSize,
            AssemblyDiskSizeMB = Math.Round(asmTotalSize / (1024.0 * 1024), 2),
            AssemblyCategories = asmCategories,

            // Worker pool context
            HttpWorkers = httpPoolSize,
            BgWorkers = bgPoolSize,
            TotalWorkers = totalWorkers,
            EstPerWorkerMB = totalWorkers > 0
                ? Math.Round((heapBytes / (1024.0 * 1024)) / totalWorkers, 1)
                : 0,

            CpuPct = GetCpuPct(),
        };
    }

    /// <summary>Get metrics for a specific pool type ("http" or "bg").</summary>
    public PoolMetrics? GetPoolMetrics(string poolType)
    {
        var snapshot = GetSnapshot();
        return poolType.Equals("http", StringComparison.OrdinalIgnoreCase)
            ? snapshot.HttpPool
            : snapshot.BgPool;
    }

    /// <summary>Get a summary of just the busy/available counts.</summary>
    public WorkerSummary GetSummary()
    {
        return new WorkerSummary
        {
            HttpBusy = _pool.HttpPoolSize - _pool.HttpAvailable,
            HttpAvailable = _pool.HttpAvailable,
            HttpPoolSize = _pool.HttpPoolSize,
            BgBusy = _pool.BgPoolSize - _pool.BgAvailable,
            BgAvailable = _pool.BgAvailable,
            BgPoolSize = _pool.BgPoolSize,
            LimiterActive = _limiter.Active,
            LimiterWaiting = _limiter.Waiting,
            LimiterMax = _limiter.CurrentMax,
            IsHttpThrottled = _limiter.IsHttpThrottled,
            JobsQueued = _jobManager.QueuedCount,
            JobsActive = _jobManager.ActiveCount,
        };
    }

    // ── Job management (exposed to PowerShell) ──

    /// <summary>Get detailed job list with wait/duration times.</summary>
    public List<JobDetail> GetJobDetails(string? runName = null, string? status = null, int limit = 100)
        => _jobManager.GetJobDetails(runName, status, limit);

    /// <summary>Get run group summaries.</summary>
    public List<JobRunSummary> GetRunSummaries()
        => _jobManager.GetRunSummaries();

    /// <summary>Cancel a single queued job by ID.</summary>
    public bool CancelJob(string jobId)
        => _jobManager.CancelJob(jobId);

    /// <summary>Cancel all queued jobs in a run group.</summary>
    public int CancelRun(string runName)
        => _jobManager.CancelRun(runName);

    /// <summary>Delete a completed/failed/cancelled job from tracking.</summary>
    public bool DeleteJob(string jobId)
        => _jobManager.DeleteJob(jobId);

    /// <summary>Change a queued job's priority (re-enqueues with new priority).</summary>
    public bool ChangePriority(string jobId, int newPriority)
        => _jobManager.ChangePriority(jobId, newPriority);

    // ── Private helpers ──

    private WorkerDetail BuildWorkerDetail(WorkerStats stats)
    {
        var uptimeMs = (long)(DateTime.UtcNow - _startTimeUtc).TotalMilliseconds;
        var utilizationPct = uptimeMs > 0
            ? Math.Round(Interlocked.Read(ref stats._totalBusyMs) * 100.0 / uptimeMs, 1)
            : 0;
        var totalAlloc = Interlocked.Read(ref stats._totalAllocBytes);
        var totalInv = Interlocked.Read(ref stats._totalInvocations);

        return new WorkerDetail
        {
            WorkerId = stats.WorkerId,
            IsBusy = stats.IsBusy,
            CurrentFunction = stats.IsBusy ? stats.CurrentFunction : null,
            TotalInvocations = totalInv,
            TotalBusyMs = Interlocked.Read(ref stats._totalBusyMs),
            TotalFaults = Interlocked.Read(ref stats._totalFaults),
            UtilizationPct = utilizationPct,
            LastDurationMs = stats.LastDurationMs,
            MinDurationMs = stats.MinDurationMs == long.MaxValue ? 0 : stats.MinDurationMs,
            MaxDurationMs = stats.MaxDurationMs,
            AvgDurationMs = totalInv > 0
                ? Interlocked.Read(ref stats._totalBusyMs) / totalInv
                : 0,
            TotalAllocMB = Math.Round(totalAlloc / (1024.0 * 1024), 2),
            LastAllocMB = Math.Round(stats.LastAllocBytes / (1024.0 * 1024), 2),
            AvgAllocMB = totalInv > 0
                ? Math.Round(totalAlloc / (1024.0 * 1024) / totalInv, 2)
                : 0,
            LastCheckoutUtc = stats.LastCheckoutUtc,
            LastReclaimUtc = stats.LastReclaimUtc,
        };
    }

    private static void AggregatePoolStats(PoolMetrics pool, List<WorkerDetail> workers,
        long retiredInvocations, long retiredBusyMs, long retiredFaults)
    {
        // Sum live workers and add retired-worker totals (workers that were recycled out
        // of the pool). Without the retired buckets, recycling a worker would decrease
        // pool.TotalInvocations and break delta-based history (StatsHistoryService).
        var liveInv = workers.Sum(w => w.TotalInvocations);
        var liveBusy = workers.Sum(w => w.TotalBusyMs);
        var liveFaults = workers.Sum(w => w.TotalFaults);

        pool.TotalInvocations = liveInv + retiredInvocations;
        pool.TotalBusyMs = liveBusy + retiredBusyMs;
        pool.TotalFaults = liveFaults + retiredFaults;
        pool.AvgUtilizationPct = workers.Count > 0
            ? Math.Round(workers.Average(w => w.UtilizationPct), 1)
            : 0;
        pool.AvgDurationMs = pool.TotalInvocations > 0
            ? pool.TotalBusyMs / pool.TotalInvocations
            : 0;
    }

    private static void UpdateDurationStats(WorkerStats stats, long durationMs)
    {
        // Lock-free min/max updates using compare-exchange
        long current;
        do { current = stats.MinDurationMs; }
        while (durationMs < current && Interlocked.CompareExchange(ref stats.MinDurationMs, durationMs, current) != current);

        do { current = stats.MaxDurationMs; }
        while (durationMs > current && Interlocked.CompareExchange(ref stats.MaxDurationMs, durationMs, current) != current);
    }

    private long _lastTrimTicks;

    /// <summary>
    /// Force a full GC collection with LOH compaction and working-set trim.
    /// Called automatically every 100 invocations and after orchestrator runs complete.
    /// Has a built-in 2-minute cooldown to avoid GC thrashing.
    /// Returns the MB reclaimed, or -1 if skipped due to cooldown.
    /// </summary>
    public long TrimMemory()
    {
        // Cooldown: skip if last trim was less than 2 minutes ago
        var now = Stopwatch.GetTimestamp();
        var lastTrim = Interlocked.Read(ref _lastTrimTicks);
        if (lastTrim > 0 && Stopwatch.GetElapsedTime(lastTrim).TotalMinutes < 2)
            return -1;

        // CAS to claim the trim — only one caller wins
        if (Interlocked.CompareExchange(ref _lastTrimTicks, now, lastTrim) != lastTrim)
            return -1;

        var proc = Process.GetCurrentProcess();
        var rssBefore = proc.WorkingSet64;

        // Compact the Large Object Heap on next collection
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        // Aggressive full collection — blocking, compacting
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        proc.Refresh();
        var rssAfter = proc.WorkingSet64;
        return (rssBefore - rssAfter) / (1024 * 1024);
    }

}
