using System.Runtime.CompilerServices;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Covers the descriptor queue's dispatch-time rehydration: what the resolver is handed, what it costs,
/// and what happens when the descriptor has gone stale underneath it.
///
/// The design question these answer is "what does an extra storage read per dispatch cost at ~200
/// dispatches/min?" — the answer being that the steady-state path performs ZERO reads, because the run
/// is already live in <c>_activeRuns</c> and object identity must be preserved anyway (see
/// <c>OrchestratorService.ResolveTaskWorkAsync</c>). Reads happen only on the recovery path.
/// </summary>
public class JobDescriptorRehydrationTests
{
    /// <summary>An in-memory <see cref="ICraftTableStore"/> that counts point reads.</summary>
    private sealed class CountingStore : ICraftTableStore
    {
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();
        public int PointReads;
        public int PartitionScans;

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureTableAsync(string table, CancellationToken ct = default)
        {
            if (!_tables.ContainsKey(table)) _tables[table] = new();
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            _tables[table][(row.PartitionKey, row.RowKey)] = row;
            return Task.CompletedTask;
        }

        public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default)
        {
            foreach (var r in rows) _tables[table][(r.PartitionKey, r.RowKey)] = r;
            return Task.CompletedTask;
        }

        public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PointReads);
            return Task.FromResult(_tables[table].TryGetValue((partitionKey, rowKey), out var r) ? r : null);
        }

        public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref PartitionScans);
            foreach (var kv in _tables[table].Where(k => k.Key.Item1 == partitionKey).ToList())
            {
                yield return kv.Value;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _tables[table].ToList())
            {
                yield return kv.Value;
                await Task.Yield();
            }
        }

        public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            _tables[table].Remove((partitionKey, rowKey));
            return Task.CompletedTask;
        }

        public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
        {
            foreach (var k in _tables[table].Keys.Where(k => k.Item1 == partitionKey).ToList())
                _tables[table].Remove(k);
            return Task.CompletedTask;
        }
    }

    private static (JobManager Jobs, CountingStore Store, OrchestratorTableStore Orch) NewHarness()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 8;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        var store = new CountingStore();
        var orch = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, store);
        return (jobs, store, orch);
    }

    private static Task Pump(JobManager jobs) => Task.Run(() => jobs.StartAsync(CancellationToken.None));

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>The descriptor reaches the resolver intact — that is the whole contract of the queue.</summary>
    [Fact]
    public async Task Dispatch_HandsTheDescriptorToTheResolver()
    {
        var (jobs, _, _) = NewHarness();
        var seen = new List<JobDescriptor>();
        var done = 0;

        jobs.SetWorkResolver((d, _) =>
        {
            lock (seen) seen.Add(d);
            return Task.FromResult<Func<CancellationToken, Task>?>(
                _ => { Interlocked.Increment(ref done); return Task.CompletedTask; });
        });

        for (var i = 0; i < 25; i++)
            jobs.Enqueue(new JobDescriptor("CIPPDBCacheRun", $"Graph_tenant{i:D3}", 5), $"CIPPDBCacheRun-Graph_tenant{i:D3}");

        _ = Pump(jobs);
        Assert.True(await WaitUntil(() => Volatile.Read(ref done) == 25), $"only {done}/25 ran");
        await Task.WhenAny(jobs.StopAsync(CancellationToken.None), Task.Delay(5000));

        lock (seen)
        {
            Assert.Equal(25, seen.Count);
            Assert.All(seen, d => Assert.Equal("CIPPDBCacheRun", d.RunName));
            Assert.Equal(25, seen.Select(d => d.TaskId).Distinct().Count());
        }
    }

    /// <summary>
    /// A descriptor whose task has gone (finalized, cancelled, or cleaned up while it sat queued) is
    /// Skipped, not Failed, and must not take the dispatch loop down with it.
    /// </summary>
    [Fact]
    public async Task StaleDescriptor_IsSkipped_AndDispatchContinues()
    {
        var (jobs, _, _) = NewHarness();
        var ran = 0;

        jobs.SetWorkResolver((d, _) => Task.FromResult<Func<CancellationToken, Task>?>(
            d.TaskId == "gone"
                ? null                                            // stale — resolver declines
                : _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }));

        jobs.Enqueue(new JobDescriptor("run", "gone", 0), "run-gone");
        for (var i = 0; i < 10; i++)
            jobs.Enqueue(new JobDescriptor("run", $"live{i}", 5), $"run-live{i}");

        _ = Pump(jobs);
        Assert.True(await WaitUntil(() => Volatile.Read(ref ran) == 10), $"only {ran}/10 ran after a stale descriptor");

        var stale = jobs.GetJobs(status: "Skipped");
        await Task.WhenAny(jobs.StopAsync(CancellationToken.None), Task.Delay(5000));

        Assert.Single(stale);
        Assert.Equal("run-gone", stale[0].Name);
    }

    /// <summary>A descriptor with no resolver registered must fail loudly, not vanish.</summary>
    [Fact]
    public async Task DescriptorWithNoResolver_FailsTheJob_RatherThanDisappearing()
    {
        var (jobs, _, _) = NewHarness();
        jobs.Enqueue(new JobDescriptor("run", "task", 0), "run-task");

        _ = Pump(jobs);
        Assert.True(await WaitUntil(() => jobs.GetJobs(status: "Failed").Count == 1));

        var failed = jobs.GetJobs(status: "Failed").Single();
        await Task.WhenAny(jobs.StopAsync(CancellationToken.None), Task.Delay(5000));

        Assert.Contains("resolver", failed.LastError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cost question. Rehydrating a task that is NOT already in memory costs exactly one partition
    /// read of the run — the same read the crash-recovery path already performs. This pins the cost so a
    /// future change that turns it into a per-task read shows up as a failure.
    /// </summary>
    [Fact]
    public async Task Rehydration_FromStorage_CostsOneRunReadPerRun_NotPerTask()
    {
        var (_, counting, store) = NewHarness();
        await store.InitializeAsync();

        var tasks = Enumerable.Range(0, 200).Select(i => new OrchestratorTaskItem
        {
            Id = $"Graph_tenant{i:D3}",
            Status = "Pending",
            Parameters = new Dictionary<string, object> { ["TenantFilter"] = $"tenant{i:D3}.onmicrosoft.com" },
        }).ToList();

        await store.UpsertRunAsync(new OrchestratorRun
        {
            Name = "CIPPDBCacheRun",
            Status = "Running",
            Priority = 5,
            StartedUtc = DateTime.UtcNow,
            Tasks = tasks,
            TaskScriptName = "Invoke-CIPPDBCacheTask",
        });
        await store.UpsertTaskBatchAsync("CIPPDBCacheRun", tasks);

        counting.PointReads = 0;
        counting.PartitionScans = 0;

        var rehydrated = await store.GetRunAsync("CIPPDBCacheRun");

        Assert.NotNull(rehydrated);
        Assert.Equal(200, rehydrated!.Tasks.Count);
        Assert.Equal(1, counting.PointReads);        // the run row
        Assert.Equal(1, counting.PartitionScans);    // all 200 task rows in one partition query
    }
}
