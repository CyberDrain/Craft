using System.Collections.Concurrent;
using System.Text.Json;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The orchestration contract as CIPP sees it, end to end through the real store, pump and JobManager with
/// only the PowerShell calls faked: a batch becomes tasks invoked with <c>TaskJson</c>; their output reaches
/// the PostExecution script as one JSON line each, with <c>FunctionName</c> and <c>ParametersJson</c>; a run
/// queued by a task holds its parent until it finishes; runs of one name stack up unless the caller asks
/// for no collisions.
/// </summary>
public class OrchestrationContractTests
{
    private const string TaskFunc = "Invoke-CraftTask";
    private const string PostExecFunc = "Invoke-CraftPostExecution";

    private sealed class Svc(JobManager jobs, WorkStore store, ResultStore results, IConfiguration config, CraftSettings settings)
        : OrchestratorService(NullLogger<OrchestratorService>.Instance, null!, null!, jobs, store, results, config, settings)
    {
        public readonly ConcurrentQueue<Dictionary<string, object>> Tasks = new();
        public readonly ConcurrentQueue<(Dictionary<string, object> Parameters, string[] Lines)> PostExecs = new();
        public Func<Dictionary<string, object>, string>? Body;
        public Func<Task>? PostExecBody;
        public int Checkouts, Reclaims;

        internal override string? FindScript(string name) => name;

        internal override async Task<string> RunScriptAsync(string path, Dictionary<string, object> parameters, bool captureOutput,
            PowerShellWorker? worker = null)
        {
            if (path == PostExecFunc)
            {
                PostExecs.Enqueue((parameters, File.ReadAllLines((string)parameters["ResultsPath"])));
                if (PostExecBody != null) await PostExecBody();
                return string.Empty;
            }
            var task = JsonSerializer.Deserialize<Dictionary<string, object>>((string)parameters["TaskJson"])!;
            Tasks.Enqueue(task);
            var output = Body?.Invoke(task) ?? JsonSerializer.Serialize(new { tenant = task["TenantFilter"].ToString() });
            return captureOutput ? output : string.Empty;
        }

        internal override PowerShellWorker? CheckoutSequentialWorker(CancellationToken ct) { Checkouts++; return null; }
        internal override void ReclaimSequentialWorker(PowerShellWorker? worker, bool faulted) => Reclaims++;
    }

    private sealed record Harness(Svc Svc, WorkStore Store, WorkPump Pump, JobManager Jobs) : IAsyncDisposable
    {
        public async Task<bool> DriveUntil(Func<Task<bool>> done, int timeoutMs = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                await Pump.RefillAsync(CancellationToken.None);
                if (await done()) return true;
                await Task.Delay(10);
            }
            return await done();
        }

        public Task<bool> DriveUntilFinished(string name, int timeoutMs = 10_000) =>
            DriveUntil(async () => await Store.GetRunByNameAsync(name) is { IsFinished: true }, timeoutMs);

        public async ValueTask DisposeAsync() => await Jobs.StopAsync(CancellationToken.None);
    }

    private static async Task<Harness> NewAsync()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 4;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        var mem = new MemoryTableStore();
        var store = new WorkStore(NullLogger<WorkStore>.Instance, settings, mem);
        var results = new ResultStore(NullLogger<ResultStore>.Instance, settings, mem);
        var svc = new Svc(jobs, store, results, config, settings);
        await svc.ResumeInterruptedRunsAsync(CancellationToken.None);
        var pump = new WorkPump(NullLogger<WorkPump>.Instance, store, jobs, config, settings, svc);
        _ = Task.Run(() => jobs.StartAsync(CancellationToken.None));
        return new Harness(svc, store, pump, jobs);
    }

    private static string Batch(int n, string prefix = "t") =>
        JsonSerializer.Serialize(Enumerable.Range(0, n).Select(i => new { Name = "Job", TenantFilter = $"{prefix}{i}", N = i }));

    private static Task<bool> Start(Harness h, string name, string batch, string? postExec = null, string? postParams = null,
        bool sequential = false, int priority = 4, bool allowCollision = true) =>
        h.Svc.StartFromBatchAsync(name, batch, priority, postExec, postParams, CancellationToken.None, sequential: sequential,
            allowCollision: allowCollision);

    [Fact]
    public async Task EveryTaskRunsOnce_WithItsBatchItemAsTaskJson_AndTheRunCompletes()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "FanOut", Batch(10)));

        Assert.True(await h.DriveUntilFinished("FanOut"));
        var run = (await h.Store.GetRunByNameAsync("FanOut"))!;
        Assert.Equal("Completed", run.Status);
        Assert.Equal(10, run.Done);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"t{i}").Order(),
            h.Svc.Tasks.Select(t => t["TenantFilter"].ToString()!).Order());
        Assert.Empty(await ReadyNames(h));
    }

    [Fact]
    public async Task PostExecution_RunsOnceAfterEveryTask_WithOneLinePerTaskOutput_AndItsParameters()
    {
        await using var h = await NewAsync();
        var bigParameters = JsonSerializer.Serialize(new { blob = new string('x', 100_000) });
        Assert.True(await Start(h, "WithPost", Batch(5), "AuditLogs", bigParameters));

        Assert.True(await h.DriveUntilFinished("WithPost"));
        var post = Assert.Single(h.Svc.PostExecs);
        Assert.Equal("AuditLogs", post.Parameters["FunctionName"]);
        Assert.Equal(bigParameters, post.Parameters["ParametersJson"]);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"t{i}").Order(),
            post.Lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("tenant").GetString()!).Order());
        Assert.Equal(5, h.Svc.Tasks.Count);
        Assert.Equal("Completed", (await h.Store.GetRunByNameAsync("WithPost"))!.PostExecStatus);
    }

    [Fact]
    public async Task AFailingTask_IsRecorded_AndTheRunStillAggregatesAndFinishesWithErrors()
    {
        await using var h = await NewAsync();
        h.Svc.Body = t => t["TenantFilter"].ToString() == "t1" ? throw new InvalidOperationException("boom") : "{}";
        Assert.True(await Start(h, "Partial", Batch(3), "Agg"));

        Assert.True(await h.DriveUntilFinished("Partial"));
        var run = (await h.Store.GetRunByNameAsync("Partial"))!;
        Assert.Equal("CompletedWithErrors", run.Status);
        Assert.Equal(1, run.Failed);
        Assert.Single(h.Svc.PostExecs);
        var failed = Assert.Single(await h.Store.GetTasksAsync(run.RunKey, 'D'), t => t.Status == "Failed");
        Assert.Equal("boom", failed.LastError);
    }

    [Fact]
    public async Task APostExecutionThatKeepsFailing_IsRetried_ThenTheRunFinishes()
    {
        await using var h = await NewAsync();
        h.Svc.PostExecBody = () => throw new InvalidOperationException("aggregate down");
        Assert.True(await Start(h, "PostFails", Batch(2), "Agg"));

        // A failed aggregation is handed back to pending, and the pump skips a run whose counts have not
        // moved for a while, so the retries are spaced out; drive with that backoff out of the way.
        Assert.True(await h.DriveUntil(async () =>
        {
            h.Pump.ForgetBackoff();
            return await h.Store.GetRunByNameAsync("PostFails") is { IsFinished: true };
        }));
        Assert.Equal(new CraftSettings().Orchestrator.MaxRetries, h.Svc.PostExecs.Count);
        Assert.Equal("Failed", (await h.Store.GetRunByNameAsync("PostFails"))!.PostExecStatus);
    }

    [Fact]
    public async Task WithoutCollisions_ARunNameStillGoing_IsNotStartedAgain_AndItsBatchFileIsStillDeleted()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Recurring", Batch(2), allowCollision: false));

        var file = Path.Combine(Path.GetTempPath(), $"craft-test-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(file, "{\"Name\":\"Job\",\"TenantFilter\":\"x\"}\n");
        Assert.False(await h.Svc.StartFromBatchAsync("Recurring", "", 4, null, null, CancellationToken.None, batchFilePath: file,
            allowCollision: false));
        Assert.False(File.Exists(file));
        Assert.Single(await ReadyNames(h));

        Assert.True(await h.DriveUntilFinished("Recurring"));
        Assert.True(await Start(h, "Recurring", Batch(1), allowCollision: false));
    }

    [Fact]
    public async Task ByDefault_RunsOfOneNameStackUp_AndEachRunsEveryTask()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Stacked", Batch(3), "Agg"));
        Assert.True(await Start(h, "Stacked", Batch(3), "Agg"));
        Assert.Equal(["Stacked", "Stacked"], await ReadyNames(h));

        Assert.True(await h.DriveUntil(async () => (await ReadyNames(h)).Count == 0));
        Assert.Equal(6, h.Svc.Tasks.Count);
        Assert.Equal(2, h.Svc.PostExecs.Count);
        Assert.All(h.Svc.PostExecs, p => Assert.Equal(3, p.Lines.Length));
        // Same task ids in both runs, so the jobs share a display name; each must still be its own job, or
        // the pump would track (and renew the lease of) only one of the two claims.
        Assert.Equal(2, h.Jobs.GetJobs().Count(j => j.Name == "Stacked-Job_t0"));
    }

    [Fact]
    public async Task ACollisionFreeStart_IsSkippedWhileAStackedRunOfThatNameIsGoing()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Mixed", Batch(1)));
        Assert.True(await Start(h, "Mixed", Batch(1)));
        Assert.False(await Start(h, "Mixed", Batch(1), allowCollision: false));
    }

    [Fact]
    public async Task AChildFindsItsExactParent_ByRunKey_WhenRunsOfThatNameOverlap()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Parent", Batch(1, "older"), "Agg"));
        Assert.True(await Start(h, "Parent", Batch(1, "newer"), "Agg"));
        var outings = await h.Store.GetActiveRunsAsync("Parent");
        var (older, newest) = (outings[0], outings[1]);

        var link = h.Svc.RegisterPendingChild(older.RunKey, "Child");
        Assert.Equal(older.RunKey, link!.Value.ParentRunKey);
        Assert.Equal(2, (await h.Store.GetRunAsync(older.RunKey))!.Total);
        Assert.Equal(1, (await h.Store.GetRunAsync(newest.RunKey))!.Total);

        // An older caller passing only the name gets the newest outing.
        Assert.Equal(newest.RunKey, h.Svc.RegisterPendingChild("Parent", "Other")!.Value.ParentRunKey);
        // A run re-queueing itself is never its own child, by key or by name.
        Assert.Null(h.Svc.RegisterPendingChild(older.RunKey, "Parent"));
    }

    [Fact]
    public async Task CancellingByName_CancelsEveryRunOfThatName()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Twin", Batch(4)));
        Assert.True(await Start(h, "Twin", Batch(4)));

        var (found, cancelled) = await h.Svc.CancelRunAsync("Twin");

        Assert.True(found);
        Assert.Equal(8, cancelled);
        Assert.Empty(await h.Store.GetActiveRunsAsync("Twin"));
    }

    [Fact]
    public async Task AChildQueuedByATask_HoldsTheParentsPostExecution_UntilTheChildFinishes()
    {
        await using var h = await NewAsync();
        var childGate = new TaskCompletionSource();
        h.Svc.Body = t =>
        {
            if (t["TenantFilter"].ToString() == "p0")
            {
                var link = h.Svc.RegisterPendingChild("Parent", "Child");
                Assert.NotNull(link);
                h.Svc.StartFromBatchAsync("Child", Batch(2, "c"), 4, null, null, CancellationToken.None,
                    parentRunKey: link!.Value.ParentRunKey, childKey: link.Value.ChildKey).GetAwaiter().GetResult();
            }
            if (t["TenantFilter"].ToString()!.StartsWith('c')) childGate.Task.GetAwaiter().GetResult();
            return "{}";
        };
        Assert.True(await Start(h, "Parent", Batch(1, "p"), "Agg"));

        Assert.True(await h.DriveUntil(async () => await h.Store.GetRunByNameAsync("Child") != null && h.Svc.Tasks.Count >= 2));
        await h.DriveUntil(() => Task.FromResult(false), 300);
        Assert.Empty(h.Svc.PostExecs);
        Assert.False((await h.Store.GetRunByNameAsync("Parent"))!.IsFinished);

        childGate.SetResult();
        Assert.True(await h.DriveUntilFinished("Parent"));
        Assert.True((await h.Store.GetRunByNameAsync("Child"))!.IsFinished);
        Assert.Single(h.Svc.PostExecs);
    }

    [Fact]
    public async Task AChildThatNeverStarts_ReleasesItsParent()
    {
        await using var h = await NewAsync();
        h.Svc.Body = _ =>
        {
            var link = h.Svc.RegisterPendingChild("Parent", "Stillborn")!.Value;
            h.Svc.AbandonPendingChildAsync(link.ParentRunKey, link.ChildKey).GetAwaiter().GetResult();
            return "{}";
        };
        Assert.True(await Start(h, "Parent", Batch(1), "Agg"));

        Assert.True(await h.DriveUntilFinished("Parent"));
        Assert.Single(h.Svc.PostExecs);
    }

    [Fact]
    public async Task ARunCannotBeItsOwnChild_NorAChildOfAFinishedRun()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Solo", Batch(1)));
        Assert.Null(h.Svc.RegisterPendingChild("Solo", "Solo"));
        Assert.True(await h.DriveUntilFinished("Solo"));
        Assert.Null(h.Svc.RegisterPendingChild("Solo", "FollowUp"));
    }

    [Fact]
    public async Task CancellingARun_CancelsWhatIsPending_AndTheRunStillFinalizes()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Doomed", Batch(20)));

        var (found, cancelled) = await h.Svc.CancelRunAsync("Doomed");
        Assert.True(found);
        Assert.Equal(20, cancelled);
        var run = (await h.Store.GetRunByNameAsync("Doomed"))!;
        Assert.True(run.IsFinished);
        Assert.Equal(20, run.Cancelled);
        Assert.Empty(h.Svc.Tasks);
    }

    [Fact]
    public async Task ASequentialRun_RunsEveryStepInOrder_OnOneWorker_PastAFailingStep()
    {
        await using var h = await NewAsync();
        h.Svc.Body = t => t["TenantFilter"].ToString() == "s2" ? throw new InvalidOperationException("step down") : "{}";
        Assert.True(await Start(h, "Steps", Batch(5, "s"), sequential: true));

        Assert.True(await h.DriveUntilFinished("Steps"));
        Assert.Equal(["s0", "s1", "s2", "s3", "s4"], h.Svc.Tasks.Select(t => t["TenantFilter"].ToString()));
        Assert.Equal(1, h.Svc.Checkouts);
        Assert.Equal(1, h.Svc.Reclaims);
        Assert.Equal(1, (await h.Store.GetRunByNameAsync("Steps"))!.Failed);
    }

    [Fact]
    public async Task WorkClaimedByAProcessThatDied_IsTakenBackOnceItsLeaseLapses()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Orphaned", Batch(3)));
        var run = (await h.Store.GetRunByNameAsync("Orphaned"))!;
        Assert.Equal(3, (await h.Store.ClaimAsync(run.RunKey, 3, "dead-host", TimeSpan.Zero, false)).Count);

        Assert.True(await h.DriveUntilFinished("Orphaned"));
        Assert.Equal(3, h.Svc.Tasks.Count);
        Assert.All(await h.Store.GetTasksAsync(run.RunKey, 'D'), t => Assert.Equal(2, t.Attempt));
    }

    [Fact]
    public async Task ALowerBandRunsFirst_AndWithinABandTheOlderRun_NotTheAlphabeticallyFirst()
    {
        await using var h = await NewAsync();
        Assert.True(await Start(h, "Zulu", Batch(1, "z"), priority: 4));
        await Task.Delay(5);
        Assert.True(await Start(h, "Alpha", Batch(1, "a"), priority: 4));
        Assert.True(await Start(h, "Background", Batch(1, "b"), priority: 9));
        Assert.True(await Start(h, "Urgent", Batch(1, "u"), priority: 1));

        Assert.Equal(["Urgent", "Zulu", "Alpha", "Background"], await ReadyNames(h));
    }

    private static async Task<List<string>> ReadyNames(Harness h)
    {
        var names = new List<string>();
        await foreach (var e in h.Store.ReadReadyAsync()) names.Add(e.Name);
        return names;
    }
}
