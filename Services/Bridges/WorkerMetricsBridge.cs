using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using Craft.Orchestration;
using Craft.PowerShellHost;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge exposing worker pool metrics and utilization data to PowerShell.
///
/// PS usage:
///   $metrics = [Craft.Services.WorkerMetricsBridge]::GetSnapshot()
///   $metrics.HttpPool.BusyCount
///   $metrics.HttpPool.Workers[0].TotalInvocations
///   $metrics.BgPool.Workers
///   $metrics.Limiter.IsHttpThrottled
///   $metrics.Jobs.Running
/// </summary>
public static class WorkerMetricsBridge
{
    private static PowerShellWorkerPool? s_pool;
    private static BackgroundTaskLimiter? s_limiter;
    private static JobManager? s_jobManager;
    private static JobQueueStatusReader? s_queueReader;
    private static OrchestratorService? s_orchestrator;
    private static ILogger? s_logger;

    // Per-worker tracking: workerId → stats
    private static readonly ConcurrentDictionary<int, WorkerStats> s_workerStats = new();
    private static readonly DateTime s_startTimeUtc = DateTime.UtcNow;
    private static long s_globalInvocations;

    // Retired-worker totals: when a worker is recycled, its accumulated counters are
    // moved here so pool aggregates remain monotonic across recycles. Without these,
    // pool.TotalInvocations would drop on recycle and StatsHistoryService deltas
    // (BgInvocations, BgBusyMs) would go negative.
    private static long s_retiredHttpInvocations;
    private static long s_retiredHttpBusyMs;
    private static long s_retiredHttpFaults;
    private static long s_retiredBgInvocations;
    private static long s_retiredBgBusyMs;
    private static long s_retiredBgFaults;

    public static void Initialize(PowerShellWorkerPool pool, BackgroundTaskLimiter limiter, JobManager jobManager,
        ILogger? logger = null, JobQueueStatusReader? queueReader = null, OrchestratorService? orchestrator = null)
    {
        s_pool = pool;
        s_limiter = limiter;
        s_jobManager = jobManager;
        s_queueReader = queueReader;
        s_orchestrator = orchestrator;
        s_logger = logger;
    }

    // ── Called by PowerShellWorkerPool/RunnerService at checkout/reclaim ──

    /// <summary>Pre-register a worker so it appears in snapshots even before first use.</summary>
    public static void RegisterWorker(int workerId, bool isHttp)
    {
        var stats = s_workerStats.GetOrAdd(workerId, _ => new WorkerStats { WorkerId = workerId });
        stats.IsHttp = isHttp;
    }

    /// <summary>Remove a worker's stats when it is recycled/replaced.</summary>
    public static void DeregisterWorker(int workerId)
    {
        // Accumulate the retiring worker's totals into the per-pool "retired" buckets
        // so pool-level sums (and delta-based history) stay monotonic across recycles.
        if (s_workerStats.TryRemove(workerId, out var stats))
        {
            var inv = Interlocked.Read(ref stats._totalInvocations);
            var busy = Interlocked.Read(ref stats._totalBusyMs);
            var faults = Interlocked.Read(ref stats._totalFaults);
            if (stats.IsHttp)
            {
                Interlocked.Add(ref s_retiredHttpInvocations, inv);
                Interlocked.Add(ref s_retiredHttpBusyMs, busy);
                Interlocked.Add(ref s_retiredHttpFaults, faults);
            }
            else
            {
                Interlocked.Add(ref s_retiredBgInvocations, inv);
                Interlocked.Add(ref s_retiredBgBusyMs, busy);
                Interlocked.Add(ref s_retiredBgFaults, faults);
            }
        }
    }

    /// <summary>Record that a worker was checked out (started processing).</summary>
    public static void RecordCheckout(int workerId, bool isHttp)
    {
        var stats = s_workerStats.GetOrAdd(workerId, _ => new WorkerStats { WorkerId = workerId });
        stats.IsHttp = isHttp;
        stats.LastCheckoutUtc = DateTime.UtcNow;
        stats.IsBusy = true;
        // Capture per-thread allocation baseline so RecordReclaim can compute the delta
        // attributable to this invocation. Sub-100ns intrinsic, no syscall.
        stats.CheckoutAllocBytes = GC.GetAllocatedBytesForCurrentThread();
        Interlocked.Increment(ref stats._totalInvocations);
    }

    /// <summary>Record that a worker was reclaimed (finished processing).</summary>
    public static void RecordReclaim(int workerId, bool faulted, long elapsedMs)
    {
        if (!s_workerStats.TryGetValue(workerId, out var stats)) return;

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
        var count = Interlocked.Increment(ref s_globalInvocations);
        if (count % 100 == 0)
        {
            var reclaimed = TrimMemory();
            if (reclaimed >= 0)
                s_logger?.LogInformation("[System] Memory trim at invocation {Count}: reclaimed ~{MB}MB {Memory}",
                    count, reclaimed, BackgroundTaskLimiter.GetMemorySnapshot());
        }
    }

    /// <summary>Record the function name being executed on a worker.</summary>
    public static void RecordFunction(int workerId, string functionName)
    {
        if (!s_workerStats.TryGetValue(workerId, out var stats)) return;
        stats.CurrentFunction = functionName;
    }

    // ── Public query methods ──

    /// <summary>Get a full snapshot of all worker metrics.</summary>
    public static WorkerMetricsSnapshot GetSnapshot()
    {
        var snapshot = new WorkerMetricsSnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            UptimeSeconds = (long)(DateTime.UtcNow - s_startTimeUtc).TotalSeconds,
        };

        if (s_pool != null)
        {
            var httpWorkers = new List<WorkerDetail>();
            var bgWorkers = new List<WorkerDetail>();

            foreach (var (workerId, stats) in s_workerStats)
            {
                var detail = BuildWorkerDetail(stats);
                if (stats.IsHttp)
                    httpWorkers.Add(detail);
                else
                    bgWorkers.Add(detail);
            }

            snapshot.HttpPool = new PoolMetrics
            {
                PoolSize = s_pool.HttpPoolSize,
                Available = s_pool.HttpAvailable,
                BusyCount = s_pool.HttpPoolSize - s_pool.HttpAvailable,
                Workers = httpWorkers,
            };

            snapshot.BgPool = new PoolMetrics
            {
                PoolSize = s_pool.BgPoolSize,
                Available = s_pool.BgAvailable,
                BusyCount = s_pool.BgPoolSize - s_pool.BgAvailable,
                Workers = bgWorkers,
            };

            // Aggregate pool-level stats (live workers + retired buckets so totals are monotonic)
            AggregatePoolStats(snapshot.HttpPool, httpWorkers,
                Interlocked.Read(ref s_retiredHttpInvocations),
                Interlocked.Read(ref s_retiredHttpBusyMs),
                Interlocked.Read(ref s_retiredHttpFaults));
            AggregatePoolStats(snapshot.BgPool, bgWorkers,
                Interlocked.Read(ref s_retiredBgInvocations),
                Interlocked.Read(ref s_retiredBgBusyMs),
                Interlocked.Read(ref s_retiredBgFaults));
        }

        if (s_limiter != null)
        {
            snapshot.Limiter = new LimiterMetrics
            {
                BaseConcurrency = s_limiter.BaseConcurrency,
                CeilingConcurrency = s_limiter.CeilingConcurrency,
                CurrentMax = s_limiter.CurrentMax,
                Active = s_limiter.Active,
                Waiting = s_limiter.Waiting,
                IsHttpThrottled = s_limiter.IsHttpThrottled,
            };
        }

        if (s_jobManager != null)
        {
            var summary = s_jobManager.GetSummary();

            // The JobManager only buffers a worker-pool-sized slice of the backlog — the rest lives in
            // the durable queue table. GetCached never blocks: a stale/missing snapshot kicks off a
            // background refresh and this poll reports what is known now.
            var durable = s_queueReader?.GetCached();

            snapshot.Jobs = new JobMetrics
            {
                Queued = summary.Queued + (durable?.Unclaimed ?? 0),
                QueuedLocal = summary.Queued,
                QueuedDurable = durable?.Unclaimed ?? 0,
                Running = summary.Running,
                Completed = summary.Completed,
                Failed = summary.Failed,
                Skipped = summary.Skipped,
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

    private static DateTime s_prevCpuSampleUtc = DateTime.UtcNow;
    private static TimeSpan s_prevCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private static double s_lastCpuPct;
    private static readonly object s_cpuLock = new();
    private static readonly int s_processorCount = Environment.ProcessorCount;

    /// <summary>
    /// Compute process CPU% since the last call. Returns a value 0–100 representing
    /// usage across all cores (e.g. 50% on a 2-core machine = 1 core fully busy).
    /// Uses a minimum 500ms window to avoid divide-by-tiny-number noise.
    /// </summary>
    private static double GetCpuPct()
    {
        lock (s_cpuLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - s_prevCpuSampleUtc;

            if (elapsed.TotalMilliseconds < 500)
                return s_lastCpuPct; // Too soon — return last known value

            var proc = Process.GetCurrentProcess();
            var cpuTime = proc.TotalProcessorTime;
            var cpuDelta = cpuTime - s_prevCpuTime;

            s_lastCpuPct = Math.Round(cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * s_processorCount) * 100, 1);
            s_prevCpuSampleUtc = now;
            s_prevCpuTime = cpuTime;

            return s_lastCpuPct;
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

    private static long s_prevContainerCpuUsec;
    private static DateTime s_prevContainerCpuSampleUtc = DateTime.MinValue;
    private static double s_lastContainerCpuPct;
    private static readonly object s_containerCpuLock = new();

    /// <summary>
    /// Read total CPU usage across the container cgroup. Returns 0 on non-Linux. Same
    /// 500ms minimum-window guard as GetCpuPct so consecutive calls are cheap.
    /// </summary>
    private static double GetContainerCpuPct()
    {
        lock (s_containerCpuLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - s_prevContainerCpuSampleUtc;
            if (s_prevContainerCpuSampleUtc != DateTime.MinValue && elapsed.TotalMilliseconds < 500)
                return s_lastContainerCpuPct;

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

            if (s_prevContainerCpuSampleUtc == DateTime.MinValue)
            {
                s_prevContainerCpuUsec = usec.Value;
                s_prevContainerCpuSampleUtc = now;
                return 0;
            }

            var cpuDeltaMs = (usec.Value - s_prevContainerCpuUsec) / 1000.0;
            s_lastContainerCpuPct = Math.Round(
                cpuDeltaMs / (elapsed.TotalMilliseconds * s_processorCount) * 100, 1);
            s_prevContainerCpuUsec = usec.Value;
            s_prevContainerCpuSampleUtc = now;
            return s_lastContainerCpuPct;
        }
    }

    /// <summary>
    /// Get a detailed memory breakdown including per-generation heap sizes, LOH/POH,
    /// fragmentation, pinned objects, thread info, loaded assemblies, and native memory.
    /// Designed for diagnostics — more expensive than the basic MemoryMetrics in GetSnapshot().
    /// </summary>
    public static MemoryBreakdown GetMemoryBreakdown()
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
        var httpPoolSize = s_pool?.HttpPoolSize ?? 0;
        var bgPoolSize = s_pool?.BgPoolSize ?? 0;
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
            UptimeSeconds = (long)(DateTime.UtcNow - s_startTimeUtc).TotalSeconds,

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
            ProcessorCount = s_processorCount,

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
    public static PoolMetrics? GetPoolMetrics(string poolType)
    {
        var snapshot = GetSnapshot();
        return poolType.Equals("http", StringComparison.OrdinalIgnoreCase)
            ? snapshot.HttpPool
            : snapshot.BgPool;
    }

    /// <summary>Get a summary of just the busy/available counts.</summary>
    public static WorkerSummary GetSummary()
    {
        var durable = s_queueReader?.GetCached();
        var localQueued = s_jobManager?.QueuedCount ?? 0;

        return new WorkerSummary
        {
            HttpBusy = s_pool != null ? s_pool.HttpPoolSize - s_pool.HttpAvailable : 0,
            HttpAvailable = s_pool?.HttpAvailable ?? 0,
            HttpPoolSize = s_pool?.HttpPoolSize ?? 0,
            BgBusy = s_pool != null ? s_pool.BgPoolSize - s_pool.BgAvailable : 0,
            BgAvailable = s_pool?.BgAvailable ?? 0,
            BgPoolSize = s_pool?.BgPoolSize ?? 0,
            LimiterActive = s_limiter?.Active ?? 0,
            LimiterWaiting = s_limiter?.Waiting ?? 0,
            LimiterMax = s_limiter?.CurrentMax ?? 0,
            IsHttpThrottled = s_limiter?.IsHttpThrottled ?? false,
            JobsQueued = localQueued + (durable?.Unclaimed ?? 0),
            JobsQueuedLocal = localQueued,
            JobsQueuedDurable = durable?.Unclaimed ?? 0,
            JobsActive = s_jobManager?.ActiveCount ?? 0,
        };
    }

    // ── Job management (exposed to PowerShell) ──
    //
    // The listing/summary methods prefer the table-merged view (JobQueueStatusReader) and fall back to
    // the in-memory JobManager, which only ever holds a worker-pool-sized buffer of the real backlog.
    // The mutating methods try the local buffer first, then the durable queue — an unclaimed task is
    // invisible to the JobManager but very much cancellable.
    //
    // These bridge calls run on a PowerShell worker thread with no synchronization context, so blocking
    // on the async read model is safe; the timeout keeps a storage stall from wedging the worker.

    /// <summary>Get detailed job list with wait/duration times, including the unclaimed durable backlog.</summary>
    public static List<JobDetail> GetJobDetails(string? runName = null, string? status = null, int limit = 100)
    {
        if (s_queueReader is { } reader)
        {
            var merged = RunBridged(ct => reader.GetJobDetailsAsync(runName, status, limit, ct));
            if (merged != null) return merged;
        }
        return s_jobManager?.GetJobDetails(runName, status, limit) ?? new();
    }

    /// <summary>Get run group summaries, sized by the durable run counters rather than local records.</summary>
    public static List<JobRunSummary> GetRunSummaries()
    {
        if (s_queueReader is { } reader)
        {
            var merged = RunBridged(ct => reader.GetRunSummariesAsync(ct));
            if (merged != null) return merged;
        }
        return s_jobManager?.GetRunSummaries() ?? new();
    }

    /// <summary>
    /// Cancel a single queued job by ID. Tries this instance's buffer first (the state writer persists
    /// that path), then the durable queue for a task no instance has claimed yet — the majority of any
    /// backlog under the pump.
    /// </summary>
    public static bool CancelJob(string jobId)
    {
        if (s_jobManager?.CancelJob(jobId) == true) return true;
        if (s_queueReader == null || s_orchestrator == null) return false;

        return RunBridged(async ct =>
        {
            var snap = await s_queueReader.GetAsync(TimeSpan.FromSeconds(2), ct);
            var row = snap?.Rows.FirstOrDefault(r => !r.Claimed && $"{r.RunName}-{r.TaskId}" == jobId);
            if (row == null) return false;

            return await s_orchestrator.TryCancelQueuedTaskAsync(row.RunName, row.TaskId);
        });
    }

    /// <summary>
    /// Cancel all queued jobs in a run group: the local buffer immediately, then the run's durable
    /// state through the orchestrator, which is what stops the table backlog from being claimed and
    /// run afterwards.
    /// </summary>
    public static int CancelRun(string runName)
    {
        var local = s_jobManager?.CancelRun(runName) ?? 0;

        if (s_orchestrator is { } orchestrator)
        {
            var durable = RunBridged(async _ =>
            {
                var (found, cancelled) = await orchestrator.CancelRunAsync(runName);
                return found ? cancelled : 0;
            });
            // Overlapping counts, not additive: the buffered jobs cancelled above were still Pending in
            // storage when the durable cancel swept the run, so the larger number is the honest one.
            return Math.Max(local, durable);
        }

        return local;
    }

    /// <summary>Delete a completed/failed/cancelled job from tracking.</summary>
    public static bool DeleteJob(string jobId)
        => s_jobManager?.DeleteJob(jobId) ?? false;

    /// <summary>
    /// Change a queued job's priority. In the local buffer this re-enqueues at the new priority; for an
    /// unclaimed durable row it moves the row to the new priority bucket (keeping its age) and records
    /// the override on the task so a restart re-queues it at the operator's priority.
    /// </summary>
    public static bool ChangePriority(string jobId, int newPriority)
    {
        if (s_jobManager?.ChangePriority(jobId, newPriority) == true) return true;
        if (s_queueReader == null) return false;

        return RunBridged(async ct =>
        {
            var snap = await s_queueReader.GetAsync(TimeSpan.FromSeconds(2), ct);
            var row = snap?.Rows.FirstOrDefault(r => !r.Claimed && $"{r.RunName}-{r.TaskId}" == jobId);
            if (row == null) return false;

            var moved = await s_queueReader.Queue.ReprioritizeTaskAsync(row.RunName, row.TaskId, newPriority, ct);
            if (moved == 0) return false;

            // Best-effort durability of the override itself: only effective where the run is live,
            // which on the dispatching node it is. The row move above is what changes dispatch order.
            s_orchestrator?.PriorityChanged(new JobDescriptor(row.RunName, row.TaskId, row.Priority), newPriority);
            return true;
        });
    }

    /// <summary>
    /// Run an async table-backed operation from this synchronous bridge. Bridge calls arrive on
    /// PowerShell worker threads with no synchronization context, so blocking here cannot deadlock;
    /// the timeout keeps a storage stall from wedging the worker, and any failure falls back to the
    /// caller's in-memory answer.
    /// </summary>
    private static T? RunBridged<T>(Func<CancellationToken, Task<T>> work, int timeoutSeconds = 15)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            return Task.Run(() => work(cts.Token)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            s_logger?.LogWarning(ex, "[WorkerMetrics] Table-backed queue operation failed — using in-memory view");
            return default;
        }
    }

    // ── Private helpers ──

    private static WorkerDetail BuildWorkerDetail(WorkerStats stats)
    {
        var uptimeMs = (long)(DateTime.UtcNow - s_startTimeUtc).TotalMilliseconds;
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

    private static long s_lastTrimTicks;

    /// <summary>
    /// Force a full GC collection with LOH compaction and working-set trim.
    /// Called automatically every 100 invocations and after orchestrator runs complete.
    /// Has a built-in 2-minute cooldown to avoid GC thrashing.
    /// Returns the MB reclaimed, or -1 if skipped due to cooldown.
    /// </summary>
    public static long TrimMemory()
    {
        // Cooldown: skip if last trim was less than 2 minutes ago
        var now = Stopwatch.GetTimestamp();
        var lastTrim = Interlocked.Read(ref s_lastTrimTicks);
        if (lastTrim > 0 && Stopwatch.GetElapsedTime(lastTrim).TotalMinutes < 2)
            return -1;

        // CAS to claim the trim — only one caller wins
        if (Interlocked.CompareExchange(ref s_lastTrimTicks, now, lastTrim) != lastTrim)
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
