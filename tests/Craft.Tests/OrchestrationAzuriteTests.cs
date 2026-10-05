using System.Text.Json;
using Craft.Configuration;
using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// The orchestration end to end on a real table backend: entity-group transactions, ETag guards, row-key range
/// queries and key escaping as Azure applies them, none of which the in-memory store can prove. Azurite by
/// default, a real account via CRAFT_TEST_TABLE_CONNECTION; each test is skipped, not failed, when neither is
/// reachable, and works in its own table prefix.
/// </summary>
[Collection(LargeAllocationSerialTests.Name)]
public class OrchestrationAzuriteTests
{
    private static async Task<OrchestrationHarness?> TryCreateAsync(int poolSize = 4, Func<ICraftTableStore, ICraftTableStore>? wrap = null)
    {
        var settings = new CraftSettings();
        var connection = Environment.GetEnvironmentVariable("CRAFT_TEST_TABLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection)) settings.Auth.UserStorageConnection = connection;
        else settings.Storage.AllowDevelopmentStorage = true;

        var tables = new AzureTableStore(settings);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tables.PingAsync(cts.Token);
        }
        catch
        {
            return null;
        }

        var prefix = "azo" + Guid.NewGuid().ToString("N")[..10];
        return await OrchestrationHarness.CreateAsync(poolSize, wrap?.Invoke(tables) ?? tables, s => s.Orchestrator.TablePrefix = prefix);
    }

    private static string Batch(int n, string prefix) => OrchestrationHarness.Batch(n, prefix);

    [Fact]
    public async Task AFanOutWithALargeAggregationPayload_RunsEveryTaskOnce_AndAggregatesThemAll()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        var big = JsonSerializer.Serialize(new { blob = new string('x', 150_000) });
        Assert.True(await h.Start("Az Fan-Out #1", Batch(60, "t"), "Agg", big));

        // A space and a '#' in the name: the run is keyed by its sanitized form.
        Assert.True(await h.DriveUntilFinished(TableKeys.Sanitize("Az Fan-Out #1"), 60_000));
        var post = Assert.Single(h.Svc.PostExecs);
        Assert.Equal(60, post.Lines.Length);
        Assert.Equal(big, post.Parameters["ParametersJson"]);
        Assert.Equal(60, h.Svc.Started.Distinct().Count());
        Assert.Equal(60, h.Svc.Started.Count);
    }

    [Fact]
    public async Task AChildHoldsItsParentsAggregation_UntilItFinishes()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Svc.BeforeRun = async t =>
        {
            var id = FakeOrchestrator.IdOf(t);
            if (id == "p0")
            {
                var parent = (await h.Store.GetActiveRunsAsync("AzParent"))[0];
                var link = h.Svc.RegisterPendingChild(parent.RunKey, "AzChild")!.Value;
                await h.Svc.StartFromBatchAsync("AzChild", Batch(2, "c"), 4, null, null, CancellationToken.None,
                    parentRunKey: link.ParentRunKey, childKey: link.ChildKey);
            }
            if (id.StartsWith('c')) await childGate.Task;
        };
        Assert.True(await h.Start("AzParent", Batch(1, "p"), "Agg"));

        Assert.True(await h.DriveUntil(async () => await h.Store.GetRunByNameAsync("AzChild") != null && h.Svc.Started.Count >= 3, 30_000));
        await h.DriveUntil(() => Task.FromResult(false), 500);
        Assert.Empty(h.Svc.PostExecs);

        childGate.SetResult();
        Assert.True(await h.DriveUntilFinished("AzParent", 30_000));
        Assert.Single(h.Svc.PostExecs);
    }

    [Fact]
    public async Task AConcurrencyLimit_HoldsOnARealBackend()
    {
        await using var h = await TryCreateAsync(poolSize: 6);
        if (h == null) return;
        h.Svc.HoldMs = 150;
        Assert.True(await h.Start("AzCapped", Batch(12, "c"), maxConcurrency: 2));

        Assert.True(await h.DriveUntilFinished("AzCapped", 60_000));
        Assert.Equal(2, h.Svc.MaxActive);
        Assert.Equal(12, h.Svc.Started.Distinct().Count());
    }

    [Fact]
    public async Task ASequentialStopOnFailureRun_StopsAndRecordsTheRestCancelled()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        h.Svc.Body = t => FakeOrchestrator.IdOf(t) == "s1" ? throw new InvalidOperationException("down") : "{}";
        Assert.True(await h.Start("AzStop", Batch(4, "s"), "Agg", sequential: true, stopOnFailure: true));

        Assert.True(await h.DriveUntilFinished("AzStop", 30_000));
        Assert.Equal(["s0", "s1"], h.Svc.Started);
        var run = (await h.Store.GetRunByNameAsync("AzStop"))!;
        Assert.Equal((1, 2, "CompletedWithErrors"), (run.Failed, run.Cancelled, run.Status));
        Assert.Single(h.Svc.PostExecs);
    }

    [Fact]
    public async Task StackedRunsOfOneName_AreFoundByName_AndCancelledTogether()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        // A dash and a tilde-free suffix, so the by-name range has neighbours that share its prefix.
        Assert.True(await h.Start("AzTwin", Batch(5, "a")));
        Assert.True(await h.Start("AzTwin", Batch(5, "b")));
        Assert.True(await h.Start("AzTwin-Other", Batch(5, "c")));
        Assert.False(await h.Start("AzTwin", Batch(1, "d"), allowCollision: false));

        Assert.Equal(2, (await h.Store.GetActiveRunsAsync("AzTwin")).Count);
        var (found, cancelled) = await h.Svc.CancelRunAsync("AzTwin");
        Assert.True(found);
        Assert.Equal(10, cancelled);
        Assert.Empty(await h.Store.GetActiveRunsAsync("AzTwin"));
        Assert.Single(await h.Store.GetActiveRunsAsync("AzTwin-Other"));
    }

    [Fact]
    public async Task AClaimLeftByADeadProcess_IsTakenBackAfterItsLease()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        Assert.True(await h.Start("AzOrphan", Batch(3, "o")));
        var run = (await h.Store.GetRunByNameAsync("AzOrphan"))!;
        Assert.Equal(3, (await h.Store.ClaimAsync(run.RunKey, 3, "dead-host", TimeSpan.FromMilliseconds(1), false)).Count);
        await Task.Delay(50);

        Assert.True(await h.DriveUntilFinished("AzOrphan", 30_000));
        Assert.All(await h.Store.GetTasksAsync(run.RunKey, 'D'), t => Assert.Equal(2, t.Attempt));
    }

    // ── the hardening guarantees, on a real backend ──

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    private static Task<RunHeader> CreateRunAsync(WorkStore s, string name, int tasks, string? postExec = null)
    {
        var started = DateTime.UtcNow;
        return s.CreateRunAsync(new RunHeader
        {
            RunKey = WorkStore.RunKeyFor(name, started),
            Name = name,
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
            PostExecFunctionName = postExec,
        }, Enumerable.Range(0, tasks).Select(i => new WorkStore.NewTask($"t{i}", [])).ToList());
    }

    [Fact]
    public async Task AFinishOfFortyNineTasksThatReachesTheBarrier_IsExactlyOneHundredEntities_AndIsAccepted()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        var run = await CreateRunAsync(h.Store, "AzHundred", 49, "Agg");
        var claims = await h.Store.ClaimAsync(run.RunKey, 49, "w", Lease, false);
        Assert.Equal(49, claims.Count);

        // 49 x (delete running + insert done) + the aggregation insert + the header = 100, the documented maximum.
        var outcome = await h.Store.FinishAsync(run.RunKey, claims.Select(c => new WorkStore.Finish(c.Seq, "Completed", Owner: "w")).ToList());

        Assert.True(outcome!.ReachedBarrier);
        Assert.Equal(RunPhase.Aggregate, (await h.Store.GetRunAsync(run.RunKey))!.Phase);
    }

    [Fact]
    public async Task ARangeReadsPageSize_IsAPageNotALimit()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        var run = await CreateRunAsync(h.Store, "AzPaged", 23);

        var rows = 0;
        await foreach (var _ in h.Tables.QueryRowKeyRangeAsync($"{h.Prefix}Work", run.RunKey, "P|", "P}", maxPerPage: 5)) rows++;

        Assert.Equal(23, rows);
    }

    [Fact]
    public async Task TheInstanceLock_HoldsOnARealBackend()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;

        var race = await Task.WhenAll(h.Store.TryHoldInstanceLockAsync("racer-a", TimeSpan.FromSeconds(2)),
            h.Store.TryHoldInstanceLockAsync("racer-b", TimeSpan.FromSeconds(2)));
        Assert.Single(race, r => r.Held);
        var winner = race.Single(r => r.Held).Holder!.Owner;
        var loser = winner == "racer-a" ? "racer-b" : "racer-a";
        Assert.False((await h.Store.TryHoldInstanceLockAsync(loser, Lease)).Held);

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.True((await h.Store.TryHoldInstanceLockAsync(loser, Lease)).Held);
        await h.Store.ReleaseInstanceLockAsync(loser);
        Assert.Null(await h.Store.GetInstanceLockAsync());
    }

    [Fact]
    public async Task AStoppedProcesssClaims_AreTakenBackByTheLockHolder_OnARealBackend()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        Assert.True(await h.Start("AzInherited", Batch(3, "i")));
        var run = (await h.Store.GetRunByNameAsync("AzInherited"))!;
        Assert.Equal(3, (await h.Store.ClaimAsync(run.RunKey, 3, "old-host/7/aaaa", TimeSpan.FromDays(1), false)).Count);

        await h.Pump.AcquireLockAsync(CancellationToken.None);
        Assert.True(await h.DriveUntilFinished("AzInherited", 30_000));
        Assert.All(await h.Store.GetTasksAsync(run.RunKey, 'D'), t => Assert.Equal(2, t.Attempt));
    }

    [Fact]
    public async Task CrashesAtWriteBoundaries_AreRepaired_OnARealBackend()
    {
        FaultyTableStore? faulty = null;
        await using var h = await TryCreateAsync(wrap: t => faulty = new FaultyTableStore(t));
        if (h == null) return;
        h.Store.IndexRetries = [TimeSpan.Zero];

        faulty!.FailUpsert = (table, _, rk) => table == $"{h.Prefix}Work" && rk == WorkStore.HeaderKey;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunAsync(h.Store, "AzHalfMade", 3));
        faulty.FailUpsert = null;

        var done = await CreateRunAsync(h.Store, "AzUnretired", 1);
        var claim = await h.Store.ClaimAsync(done.RunKey, 1, "w", Lease, false);
        faulty.FailDelete = (table, _) => table == $"{h.Prefix}Ready" || table == $"{h.Prefix}Names";
        await h.Store.FinishAsync(done.RunKey, [new WorkStore.Finish(claim[0].Seq, "Completed", Owner: "w")]);
        faulty.FailDelete = null;

        var r = await h.Store.RepairIndexesAsync(TimeSpan.Zero);

        Assert.Equal((1, 1), (r.Removed, r.Retired));
        var active = 0;
        await foreach (var _ in h.Tables.QueryPartitionAsync($"{h.Prefix}Names", "A")) active++;
        Assert.Equal(0, active);
        Assert.Equal(1, await h.Store.SweepFinishedAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task ATaskFinishedTwiceInOneBatch_IsAppliedOnce_OnARealBackend()
    {
        await using var h = await TryCreateAsync();
        if (h == null) return;
        var run = await CreateRunAsync(h.Store, "AzTwice", 2);
        var claim = (await h.Store.ClaimAsync(run.RunKey, 1, "w", Lease, false))[0];

        var outcome = await h.Store.FinishAsync(run.RunKey,
            [new WorkStore.Finish(claim.Seq, "Completed", Owner: "w"), new WorkStore.Finish(claim.Seq, "Cancelled", Owner: "w")]);

        Assert.Equal(1, outcome!.Applied);
    }
}
