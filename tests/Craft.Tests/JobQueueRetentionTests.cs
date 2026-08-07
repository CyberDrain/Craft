using Craft.Configuration;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Heap-delta measurement cannot share a process with concurrently-allocating tests — xUnit runs
/// collections in parallel, and another collection's allocations land in the same
/// <c>GC.GetTotalMemory</c> window. Observed swinging the same measurement from 725 to 1181 B/task.
/// This collection runs alone.
/// </summary>
[CollectionDefinition(RetentionMeasurement.Name, DisableParallelization = true)]
public class RetentionMeasurement
{
    public const string Name = "retention-measurement";
}

/// <summary>
/// Measures what the JobManager queue actually retains per queued job.
///
/// The claim under test is that queue entries pin whole <see cref="OrchestratorRun"/> graphs and are
/// therefore a primary driver of heap growth under a deep backlog (production peaked at 783 queued with
/// 3.7-hour waits). Retention is measured, not assumed: each case builds the graph, settles the GC, and
/// diffs <c>GC.GetTotalMemory(forceFullCollection: true)</c>.
///
/// The measurement deliberately holds ONE run graph alive across every case, mirroring
/// <c>OrchestratorService._activeRuns</c> — which pins the run from <c>DispatchPendingTasks</c>
/// (TryAdd) until <c>FinalizeRunAsync</c> (TryRemove), i.e. for the entire time its tasks sit queued.
/// So the reported delta is the queue's MARGINAL cost, which is the only part a descriptor rewrite
/// can actually reclaim.
/// </summary>
[Collection(RetentionMeasurement.Name)]
public class JobQueueRetentionTests
{
    private const int Jobs = 5000;

    /// <summary>Bytes below which a per-job cost is not worth a redesign on a 2398MB heap cap.</summary>
    private const int NoiseFloorBytesPerJob = 32;

    private static JobManager NewJobManager(int bgPoolSize = 8)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = bgPoolSize;
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, settings, new StartupProgressService(),
            new Lazy<WorkerMetricsService>(() => null!));
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, settings, pool);
        return new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
    }

    /// <summary>A task payload shaped like a real CIPP fan-out item (tenant + collection + queue refs).</summary>
    private static OrchestratorTaskItem NewTask(int i) => new()
    {
        Id = $"Graph_tenant{i:D4}.onmicrosoft.com",
        Status = "Pending",
        Parameters = new Dictionary<string, object>
        {
            ["TenantFilter"] = $"tenant{i:D4}.onmicrosoft.com",
            ["CollectionType"] = "Graph",
            ["Name"] = $"CIPPDBCacheRun-Graph_tenant{i:D4}.onmicrosoft.com",
            ["FunctionName"] = "Invoke-CIPPDBCacheTask",
            ["QueueName"] = "cippdbcache",
            ["BatchNumber"] = 1L,
        },
    };

    private static OrchestratorRun NewRun(int taskCount)
    {
        var run = new OrchestratorRun
        {
            Name = "CIPPDBCacheRun",
            Status = "Running",
            Priority = 5,
            StartedUtc = DateTime.UtcNow,
            TaskScriptName = "Invoke-CIPPDBCacheTask",
        };
        for (var i = 0; i < taskCount; i++) run.Tasks.Add(NewTask(i));
        return run;
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Heap bytes retained by whatever <paramref name="build"/> returns, over and above the state
    /// already alive when it is called.
    ///
    /// Best-of-N, taking the MINIMUM: measurement error here is one-sided. Anything else allocating
    /// during the window — the test host, finalizers, JIT on the first pass — can only inflate the
    /// delta, never shrink it below the true retained size. So the smallest observation is the closest
    /// to truth, and averaging would bake the noise in.
    /// </summary>
    private static long RetainedBytes(Func<object> build, int trials = 3)
    {
        var best = long.MaxValue;

        for (var i = 0; i < trials; i++)
        {
            // Drop the previous trial's graph and settle BEFORE sampling the floor. Without this the
            // prior graph is still rooted at `before` and reclaimed inside the window, which drives the
            // delta negative — that is how this reported "0 bytes" for a 3.6MB run graph.
            Held = null;
            Settle();

            var before = GC.GetTotalMemory(forceFullCollection: true);
            Held = build();
            Settle();
            var after = GC.GetTotalMemory(forceFullCollection: true);

            var delta = after - before;
            if (delta > 0) best = Math.Min(best, delta);
        }

        Held = null;
        Assert.NotEqual(long.MaxValue, best);   // every trial was non-positive ⇒ the measurement is broken
        return best;
    }

    /// <summary>
    /// Roots the graph under measurement across the sampling window. A static field rather than a local
    /// so the JIT cannot treat it as dead before <c>GC.GetTotalMemory</c> runs, and so the previous
    /// trial's graph can be explicitly released.
    /// </summary>
    private static object? Held;

    /// <summary>
    /// TODAY: every queued job carries a closure capturing (run, task, taskPath, this) — the shape
    /// built by <c>OrchestratorService.DispatchSingleTask</c>.
    /// </summary>
    [Fact]
    public void ClosureQueue_MarginalRetentionPerJob_IsMeasured()
    {
        var run = NewRun(Jobs);          // held alive throughout, exactly as _activeRuns does
        const string taskPath = "/app/API/Modules/CIPP/Invoke-CIPPDBCacheTask.ps1";
        var sink = new object();          // stands in for the captured `this` (OrchestratorService)

        var bytes = RetainedBytes(() =>
        {
            var jm = NewJobManager();
            foreach (var task in run.Tasks)
            {
                var captured = task;
                jm.Enqueue(
                    name: $"{run.Name}-{captured.Id}",
                    priority: run.Priority,
                    runName: run.Name,
                    work: _ =>
                    {
                        // Same captures as the production closure: run graph, task, script path, service.
                        GC.KeepAlive(run);
                        GC.KeepAlive(captured);
                        GC.KeepAlive(taskPath);
                        GC.KeepAlive(sink);
                        return Task.CompletedTask;
                    });
            }
            return jm;
        });

        GC.KeepAlive(run);
        var perJob = bytes / (double)Jobs;
        Assert.Equal(Jobs, NewJobManagerQueueDepthProbe(run));   // sanity: all enqueued, none dispatched
        Assert.InRange(perJob, NoiseFloorBytesPerJob, 4096);
        TestOutput($"closure queue: {bytes:N0} bytes for {Jobs:N0} jobs = {perJob:N0} bytes/job");
    }

    /// <summary>
    /// AFTER: the queue carries only <see cref="JobDescriptor"/> — (runName, taskId, priority) — and the
    /// work is rehydrated at dispatch. No closure, no delegate, no <c>_pendingWork</c> entry, and no
    /// Guid-suffixed second id string.
    /// </summary>
    [Fact]
    public void DescriptorQueue_RetainsLessPerJob_ThanTheClosureQueue()
    {
        var run = NewRun(Jobs);
        const string taskPath = "/app/API/Modules/CIPP/Invoke-CIPPDBCacheTask.ps1";
        var sink = new object();

        var closureBytes = RetainedBytes(() =>
        {
            var jm = NewJobManager();
            foreach (var task in run.Tasks)
            {
                var captured = task;
                jm.Enqueue($"{run.Name}-{captured.Id}", run.Priority, _ =>
                {
                    GC.KeepAlive(run); GC.KeepAlive(captured); GC.KeepAlive(taskPath); GC.KeepAlive(sink);
                    return Task.CompletedTask;
                }, run.Name);
            }
            return jm;
        });

        var descriptorBytes = RetainedBytes(() =>
        {
            var jm = NewJobManager();
            foreach (var task in run.Tasks)
                jm.Enqueue(new JobDescriptor(run.Name, task.Id, run.Priority), $"{run.Name}-{task.Id}");
            return jm;
        });

        GC.KeepAlive(run);
        var closurePerJob = closureBytes / (double)Jobs;
        var descriptorPerJob = descriptorBytes / (double)Jobs;
        var saved = 1 - (descriptorPerJob / closurePerJob);

        TestOutput($"BEFORE (closure):    {closurePerJob:N0} B/job  ({closureBytes:N0} total)");
        TestOutput($"AFTER  (descriptor): {descriptorPerJob:N0} B/job  ({descriptorBytes:N0} total)");
        TestOutput($"reduction: {saved:P0}  ({closurePerJob - descriptorPerJob:N0} B/job)");

        Assert.True(descriptorBytes < closureBytes,
            $"descriptor queue ({descriptorBytes:N0}B) must retain less than the closure queue ({closureBytes:N0}B)");
    }

    /// <summary>
    /// The load-bearing measurement, and the one that falsifies "the queue is the memory problem".
    ///
    /// The run graph a closure captures is ALREADY pinned by <c>_activeRuns</c> from
    /// <c>DispatchPendingTasks</c> to <c>FinalizeRunAsync</c>, so a descriptor rewrite reclaims the
    /// queue's own bookkeeping and nothing else. Measured, both are the same order of magnitude
    /// (~730 B), and at production's observed peak of 783 queued jobs the ENTIRE queue is well under
    /// 1 MB — 0.02% of the 2398 MB <c>DOTNET_GCHeapHardLimit</c>. The queue cannot be what OOMs the
    /// container; this test fails if that ever stops being true.
    /// </summary>
    [Fact]
    public void QueueRetention_AtProductionPeak_IsNegligibleAgainstTheHeapCap()
    {
        const int ProductionPeakQueueDepth = 783;
        const long HeapHardLimitBytes = 0x95E00000;   // build/Dockerfile DOTNET_GCHeapHardLimit = 2398 MB

        var runBytes = RetainedBytes(() => NewRun(Jobs));

        var run = NewRun(Jobs);
        var queueBytes = RetainedBytes(() =>
        {
            var jm = NewJobManager();
            foreach (var task in run.Tasks)
            {
                var captured = task;
                jm.Enqueue($"{run.Name}-{captured.Id}", run.Priority, _ =>
                {
                    GC.KeepAlive(run); GC.KeepAlive(captured);
                    return Task.CompletedTask;
                }, run.Name);
            }
            return jm;
        });
        GC.KeepAlive(run);

        var perJob = queueBytes / (double)Jobs;
        var atPeak = (long)(perJob * ProductionPeakQueueDepth);
        var shareOfCap = atPeak / (double)HeapHardLimitBytes;

        TestOutput($"run graph ({Jobs:N0} tasks): {runBytes:N0} bytes ({runBytes / (double)Jobs:N0} B/task, pinned by _activeRuns)");
        TestOutput($"queue bookkeeping:          {queueBytes:N0} bytes ({perJob:N0} B/job, reclaimable)");
        TestOutput($"at production peak ({ProductionPeakQueueDepth} queued): {atPeak:N0} bytes = {shareOfCap:P3} of the 2398MB cap");

        Assert.True(shareOfCap < 0.01,
            $"queue at peak is {shareOfCap:P2} of the heap cap ({atPeak:N0}B) — if this ever exceeds 1% " +
            "the queue really has become a memory driver and this analysis needs redoing");
    }

    private static int NewJobManagerQueueDepthProbe(OrchestratorRun run)
    {
        var jm = NewJobManager();
        foreach (var t in run.Tasks) jm.Enqueue(t.Id, run.Priority, _ => Task.CompletedTask, run.Name);
        return jm.QueuedCount;
    }

    private static void TestOutput(string message) => Console.WriteLine($"[retention] {message}");
}
