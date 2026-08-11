using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The pump polls storage for work, so an idle instance was scanning the queue table once a second
/// forever. Backing off fixes that, but the interval is also a hard throughput ceiling — a refill hands
/// over at most batchSize jobs per tick, so a flat 10s poll caps the whole system at batchSize/10 tasks
/// per second. On a 7,336-task fan-out that is hours of waiting no matter how fast the tasks are.
///
/// So the backoff has to key off the right signal. "Claimed nothing this tick" is NOT idleness: the
/// pump also claims nothing while its buffer is above the low-water mark, which is precisely when a
/// busy run is about to need its next batch. Idle means claimed nothing AND holding nothing.
///
/// These tests pin both directions — that a quiet pump slows down, and that a working one does not.
/// </summary>
public class JobQueuePumpBackoffTests
{
    private static (JobQueuePump Pump, JobQueueStore Queue, JobManager Jobs) NewPump(
        int pollMs, int idlePollMs, int backlog = 0, int batch = 4, int lowWater = 2, int? poolSize = null)
    {
        var settings = new CraftSettings();
        // Separate knobs on purpose: with batch == pool every claimed job starts immediately, the buffer
        // empties into the running state and the pump keeps claiming. A pool SMALLER than the batch is
        // what leaves work sitting in the buffer — the state where the pump holds work but claims none.
        settings.Worker.BgPoolSize = poolSize ?? batch;

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JobQueueBatchSize"] = batch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["JobQueueLowWaterMark"] = lowWater.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["JobQueuePollIntervalMs"] = pollMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["JobQueueIdlePollIntervalMs"] = idlePollMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();

        var backing = new CountingStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        queue.InitializeAsync().GetAwaiter().GetResult();

        if (backlog > 0)
        {
            queue.EnqueueBatchAsync("run",
                Enumerable.Range(0, backlog).Select(i => ($"task-{i:D5}", 4)).ToList(),
                new DateTime(2026, 8, 9, 2, 0, 0, DateTimeKind.Utc)).GetAwaiter().GetResult();
        }

        backing.Scans = 0;   // ignore the enqueue traffic

        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);

        var pump = new JobQueuePump(NullLogger<JobQueuePump>.Instance, queue, jobs, config, settings);
        return (pump, queue, jobs);
    }

    private static async Task PumpFor(JobQueuePump pump, int ms)
    {
        await pump.StartAsync(CancellationToken.None);
        await Task.Delay(ms);
        await Task.WhenAny(pump.StopAsync(CancellationToken.None), Task.Delay(3000));
    }

    /// <summary>Counts table scans, which is exactly what the backoff is meant to reduce.</summary>
    private sealed class CountingStore : ICraftTableStore
    {
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();
        private readonly object _sync = new();

        public int Scans;

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureTableAsync(string table, CancellationToken ct = default)
        {
            lock (_sync) { if (!_tables.ContainsKey(table)) _tables[table] = new(); }
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (!_tables.ContainsKey(table)) _tables[table] = new();
                _tables[table][(row.PartitionKey, row.RowKey)] = row;
            }
            return Task.CompletedTask;
        }

        public Task UpsertBatchAsync(string table, string pk, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (!_tables.ContainsKey(table)) _tables[table] = new();
                foreach (var r in rows) _tables[table][(r.PartitionKey, r.RowKey)] = r;
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryReplaceBatchAsync(string table, string pk, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (!_tables.ContainsKey(table)) _tables[table] = new();
                foreach (var r in rows) _tables[table][(r.PartitionKey, r.RowKey)] = r;
            }
            return Task.FromResult(true);
        }

        public Task<StoreRow?> GetAsync(string table, string pk, string rk, CancellationToken ct = default)
        {
            lock (_sync)
                return Task.FromResult(_tables.TryGetValue(table, out var t)
                    && t.TryGetValue((pk, rk), out var r) ? r : null);
        }

        public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string pk,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            List<StoreRow> snap;
            lock (_sync)
                snap = _tables.TryGetValue(table, out var t)
                    ? t.Where(k => k.Key.Item1 == pk).Select(k => k.Value).ToList()
                    : new List<StoreRow>();
            foreach (var r in snap) { yield return r; await Task.Yield(); }
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Scans);
            List<StoreRow> snap;
            lock (_sync) snap = _tables.TryGetValue(table, out var t) ? t.Values.ToList() : new List<StoreRow>();
            foreach (var r in snap) { yield return r; await Task.Yield(); }
        }

        public Task DeleteAsync(string table, string pk, string rk, CancellationToken ct = default)
        {
            lock (_sync) { if (_tables.TryGetValue(table, out var t)) t.Remove((pk, rk)); }
            return Task.CompletedTask;
        }

        public Task DeletePartitionAsync(string table, string pk, CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (!_tables.TryGetValue(table, out var t)) return Task.CompletedTask;
                foreach (var k in t.Keys.Where(k => k.Item1 == pk).ToList()) t.Remove(k);
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AnIdlePump_BacksOff_InsteadOfScanningEveryTick()
    {
        // Empty queue: every tick claims nothing and holds nothing.
        var (pump, queue, _) = NewPump(pollMs: 100, idlePollMs: 2000);
        var backing = (CountingStore)GetBacking(queue);

        await PumpFor(pump, 900);

        // At a flat 100ms this window is ~9 scans. Doubling (100/200/400/800/1600...) allows about 4.
        Assert.True(backing.Scans <= 5,
            $"idle pump scanned storage {backing.Scans} times in 900ms — it is not backing off");
        Assert.True(backing.Scans >= 1, "idle pump never polled at all");
    }

    /// <summary>
    /// The regression a naive backoff would cause, measured where it actually shows: throughput while a
    /// consumer is draining the buffer.
    ///
    /// A refill hands over at most batchSize jobs, and only happens once per tick, so the poll interval
    /// is a hard ceiling of batchSize/interval. If the pump backed off because a tick claimed nothing —
    /// which it legitimately does whenever the buffer is above the low-water mark — a busy run would be
    /// throttled to batchSize per IDLE interval instead. Here that is the difference between ~9 refills
    /// in the window and ~4.
    ///
    /// Scans rather than polls is the right unit: RefillAsync returns without touching storage while the
    /// buffer is full, so a scan happens exactly when the pump needed more work.
    /// </summary>
    [Fact]
    public async Task APumpWhoseBufferIsDraining_RefillsAtTheBaseInterval()
    {
        var (pump, queue, jobs) = NewPump(pollMs: 100, idlePollMs: 2000, backlog: 200);
        var backing = (CountingStore)GetBacking(queue);

        // A consumer, so the buffer actually draws down and refills are needed — without one the pump
        // fills once and correctly never scans again.
        jobs.SetWorkResolver((_, _) => Task.FromResult<Func<CancellationToken, Task>?>(_ => Task.CompletedTask));
        _ = Task.Run(() => jobs.StartAsync(CancellationToken.None));

        await PumpFor(pump, 900);
        await jobs.StopAsync(CancellationToken.None);

        Assert.True(backing.Scans >= 6,
            $"a draining buffer was only refilled {backing.Scans} times in 900ms — the pump backed off " +
            "while work was flowing, which caps throughput at batchSize per idle interval");
    }

    /// <summary>
    /// The case that decides the idle SIGNAL, as opposed to the backoff curve.
    ///
    /// While long tasks run, the buffer stays above the low-water mark and the pump claims nothing tick
    /// after tick. Treating "claimed nothing" as idle backs the loop off during exactly that stretch, and
    /// the delay is then paid at the worst moment: the buffer finally drains and the refill that should
    /// have taken one base interval takes an idle one. That is why idleness requires holding nothing as
    /// well as claiming nothing.
    ///
    /// Long tasks are modelled by blocking the consumer, then released so the buffer drains. The window
    /// after release is short enough that a backed-off pump cannot have polled in it at all.
    /// </summary>
    [Fact]
    public async Task AfterALongStretchOfHoldingWork_TheNextRefillIsStillPrompt()
    {
        // Batch 8 into a pool of 1: one job runs, seven sit in the buffer above the low-water mark, so
        // the pump claims nothing while still holding claims. That is the stretch under test.
        var (pump, queue, jobs) = NewPump(pollMs: 100, idlePollMs: 3000, backlog: 200, batch: 8, poolSize: 1);
        var backing = (CountingStore)GetBacking(queue);

        var release = new SemaphoreSlim(0);
        jobs.SetWorkResolver((_, _) => Task.FromResult<Func<CancellationToken, Task>?>(
            async _ => await release.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)));
        _ = Task.Run(() => jobs.StartAsync(CancellationToken.None));

        await pump.StartAsync(CancellationToken.None);

        // Tasks are stuck, so the buffer stays full and nothing is claimed for many ticks.
        await Task.Delay(800);
        var scansWhileHolding = backing.Scans;

        // Let everything finish; the buffer now drains and the pump must top it up at the base interval.
        release.Release(1000);
        await Task.Delay(300);
        var scansAfterDrain = backing.Scans - scansWhileHolding;

        await Task.WhenAny(pump.StopAsync(CancellationToken.None), Task.Delay(3000));
        await jobs.StopAsync(CancellationToken.None);

        Assert.True(scansAfterDrain >= 2,
            $"only {scansAfterDrain} refill(s) in the 300ms after the buffer drained — the pump had backed " +
            "off during the stretch where it held work but claimed nothing, so the next batch was late");
    }

    private static object GetBacking(JobQueueStore queue) =>
        typeof(JobQueueStore).GetField("_store",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(queue)!;
}
