namespace Craft.Tests;

/// <summary>Per-run entries must go when the run goes, however it ended: here, deleted by the abandon sweep.</summary>
public class OrchestratorRetentionTests
{
    private const int N = 2_000;

    private static async Task<(OrchestrationHarness H, List<string> RunKeys)> AbandonedRunsAsync()
    {
        var h = await OrchestrationHarness.CreateAsync();
        for (var i = 0; i < N; i++) Assert.True(await h.Start($"R{i}", OrchestrationHarness.Batch(1, $"r{i}")));
        await h.Svc.LogRunStatusAsync(CancellationToken.None);
        var keys = new List<string>();
        await foreach (var e in h.Store.ReadReadyAsync(1000)) keys.Add(e.RunKey);
        Assert.Equal(N, keys.Count);
        Assert.Equal(N, Counts(h)["Orchestrator.lastStatusLog"]);
        foreach (var key in keys) await h.Store.DeleteRunAsync(key);
        return (h, keys);
    }

    private static Dictionary<string, long> Counts(OrchestrationHarness h)
    {
        var c = new Dictionary<string, long>();
        h.Svc.CountRetained(c);
        return c;
    }

    [Fact]
    public async Task ADeletedRun_LeavesNoStatusLogEntry()
    {
        var (h, _) = await AbandonedRunsAsync();
        await using var _h = h;

        await h.Svc.LogRunStatusAsync(CancellationToken.None);

        Assert.Equal(0, Counts(h)["Orchestrator.lastStatusLog"]);
    }

    [Fact]
    public async Task ADeletedRun_LeavesNoRateLimiterBucket()
    {
        var (h, _) = await AbandonedRunsAsync();
        await using var _h = h;

        Assert.Equal(0, Counts(h)["WorkStore.rateBuckets"]);
    }
}
