using System.Text.Json;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Realtime;
using Craft.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static Craft.Tests.OrchestrationHarness;

namespace Craft.Tests;

/// <summary>
/// End to end: real orchestrator, store and status reader, with the realtime run-status pump driven alongside
/// the work. A watched queue id must report progress while its runs work and send exactly one "end", only
/// once every run carrying the id has finished: a plain fan-out, a parent whose tasks queue child runs that
/// queue their own, and child runs that only reach storage after the task that queued them has returned
/// (PowerShell's QueueOrchestration is drained asynchronously).
/// </summary>
public class RealtimeRunStatusE2ETests
{
    private const string User = "alice@contoso.com";

    private sealed class Watcher : IDisposable
    {
        public readonly RealtimeService Realtime =
            new(new CraftSettings { Realtime = new RealtimeSettings { Enabled = true } }, NullLogger<RealtimeService>.Instance);
        public readonly List<JsonElement> Frames = [];
        private readonly RealtimeService.Connection _conn;

        public Watcher(OrchestrationHarness h, string queueId)
        {
            var reader = new JobQueueStatusReader(NullLogger<JobQueueStatusReader>.Instance, h.Jobs, h.Store);
            // The task list comes from the bridge's job manager. Tests in this class run one at a time.
            QueueStatusBridge.Initialize(h.Jobs, h.Svc, reader);
            Realtime.RunStatusSource = ids =>
            {
                reader.GetAsync(TimeSpan.Zero).GetAwaiter().GetResult();
                return QueueStatusBridge.RollUp(reader.GetRunSummariesAsync().GetAwaiter().GetResult(), ids);
            };
            _conn = Realtime.Connect(User).Conn!;
            Realtime.Watch(User, queueId, trackRun: true);
        }

        /// <summary>Pump once and keep what it sent.</summary>
        public List<JsonElement> Pump()
        {
            Realtime.PumpRuns();
            var fresh = new List<JsonElement>();
            while (_conn.Reader.TryRead(out var frame))
                fresh.Add(JsonDocument.Parse(frame.Split("data: ")[1]).RootElement.Clone());
            Frames.AddRange(fresh);
            return fresh;
        }

        public IEnumerable<JsonElement> Ends => Frames.Where(f => f.GetProperty("mode").GetString() == "end");

        public void Dispose() => Realtime.Dispose();
    }

    private static int Completed(JsonElement f) => f.GetProperty("data").GetProperty("completed").GetInt32() + f.GetProperty("data").GetProperty("failed").GetInt32();

    /// <summary>The end frame alone must be enough for the tracker: every task listed, all finished.</summary>
    private static void AssertTaskList(JsonElement end, int tasks, int failed = 0)
    {
        var list = end.GetProperty("data").GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(tasks, list.Count);
        Assert.Equal(failed, list.Count(t => t.GetProperty("status").GetString() == "Failed"));
        Assert.All(list, t => Assert.True(t.GetProperty("status").GetString() is "Completed" or "Failed"));
    }

    /// <summary>Drive the work and the pump together; at every "end" assert that all <paramref name="runs"/> are finished.</summary>
    private static async Task DriveWatching(OrchestrationHarness h, Watcher w, string[] runs, int timeoutMs = 20_000)
    {
        var ok = await h.DriveUntil(async () =>
        {
            foreach (var _ in w.Pump().Where(f => f.GetProperty("mode").GetString() == "end"))
                foreach (var run in runs)
                    Assert.True(await h.Store.GetRunByNameAsync(run) is { IsFinished: true },
                        $"'end' was sent while {run} was not finished");
            return w.Ends.Any();
        }, timeoutMs);
        Assert.True(ok, "the watched queue never ended");
        w.Pump();
    }

    [Fact]
    public async Task FanOut_ReportsProgressThenOneEnd()
    {
        await using var h = await CreateAsync(poolSize: 2);
        var q = Guid.NewGuid().ToString();
        var gate = new SemaphoreSlim(0);
        h.Svc.BeforeRun = _ => gate.WaitAsync();
        using var w = new Watcher(h, q);

        QueueStatusBridge.RegisterQueueMetadata(q, "Sync users", "/sync", "");
        Assert.True(await h.Start($"FanOut-{q}", Batch(12)));
        // Let the tasks through a few at a time so the pump sees the run part-way.
        for (var released = 0; released < 12; released += 3)
        {
            gate.Release(3);
            await h.DriveUntil(() => Task.FromResult(h.Svc.Tasks.Count >= released + 3), 5_000);
            await h.DriveUntil(() => Task.FromResult(false), 100);
            w.Pump();
        }
        await DriveWatching(h, w, [$"FanOut-{q}"]);

        var end = Assert.Single(w.Ends);
        Assert.Equal("Completed", end.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(12, end.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(12, Completed(end));
        AssertTaskList(end, 12);
        Assert.Equal("Sync users", end.GetProperty("data").GetProperty("label").GetString());
        Assert.Equal($"FanOut-{q}", end.GetProperty("data").GetProperty("runName").GetString());
        var progress = w.Frames.Where(f => f.GetProperty("mode").GetString() == "update").Select(Completed).ToList();
        Assert.True(progress.Count >= 2, $"expected progress updates, got [{string.Join(',', progress)}]");
        Assert.Equal(progress.OrderBy(c => c), progress);
        Assert.Equal(w.Frames[^1], end);
    }

    [Fact]
    public async Task NestedChildRunsQueuedLate_HoldTheEndUntilTheLastOneFinishes()
    {
        await using var h = await CreateAsync(poolSize: 4);
        var q = Guid.NewGuid().ToString();
        string parent = $"Parent-{q}", child = $"Child-{q}", grandchild = $"GrandChild-{q}";

        // Queue a child the way a PowerShell task does: the parent link is registered at once, the run
        // itself only lands once the bridge is drained, after the queuing task has returned.
        void QueueLate(string from, string name, int tasks, string prefix)
        {
            var link = h.Svc.RegisterPendingChild(from, name);
            Assert.NotNull(link);
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                await h.Svc.StartFromBatchAsync(name, Batch(tasks, prefix), 4, null, null, CancellationToken.None,
                    parentRunKey: link!.Value.ParentRunKey, childKey: link.Value.ChildKey);
            });
        }

        h.Svc.Body = t =>
        {
            var id = t["TenantFilter"].ToString();
            if (id == "p0") QueueLate(parent, child, 2, "c");
            if (id == "c0") QueueLate(child, grandchild, 5, "g");
            return "{}";
        };
        using var w = new Watcher(h, q);

        Assert.True(await h.Start(parent, Batch(3, "p")));
        await DriveWatching(h, w, [parent, child, grandchild]);

        var end = Assert.Single(w.Ends);
        Assert.Equal("Completed", end.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(10, end.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(10, Completed(end));
        AssertTaskList(end, 10);
    }

    [Fact]
    public async Task FailedTasksInAChildRun_EndWithErrors()
    {
        await using var h = await CreateAsync(poolSize: 4);
        var q = Guid.NewGuid().ToString();
        string parent = $"Parent-{q}", child = $"Child-{q}";
        h.Svc.Body = t =>
        {
            var id = t["TenantFilter"].ToString()!;
            if (id == "p0")
            {
                var link = h.Svc.RegisterPendingChild(parent, child);
                h.Svc.StartFromBatchAsync(child, Batch(3, "c"), 4, null, null, CancellationToken.None,
                    parentRunKey: link!.Value.ParentRunKey, childKey: link.Value.ChildKey).GetAwaiter().GetResult();
            }
            if (id == "c1") throw new InvalidOperationException("boom");
            return "{}";
        };
        using var w = new Watcher(h, q);

        Assert.True(await h.Start(parent, Batch(2, "p")));
        await DriveWatching(h, w, [parent, child]);

        var data = Assert.Single(w.Ends).GetProperty("data");
        Assert.Equal("CompletedWithErrors", data.GetProperty("status").GetString());
        Assert.Equal(5, data.GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("failed").GetInt32());
        AssertTaskList(Assert.Single(w.Ends), 5, failed: 1);
    }
}
