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
    private static async Task<OrchestrationHarness?> TryCreateAsync(int poolSize = 4)
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
        return await OrchestrationHarness.CreateAsync(poolSize, tables, s => s.Orchestrator.TablePrefix = prefix);
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
}
