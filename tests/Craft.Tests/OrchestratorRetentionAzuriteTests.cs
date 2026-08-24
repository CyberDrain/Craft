using Azure;
using Azure.Data.Tables;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The retention sweep against real tables (Azurite, or a storage account via
/// CRAFT_TEST_TABLE_CONNECTION). Two things only the real backend can prove: the orphan scan's
/// <c>$select</c> projection is accepted and still yields PartitionKey and Timestamp, and
/// DeletePartitionAsync really empties a partition written through the normal paths. Skipped, not
/// failed, when no backend is reachable — with the same caveat as
/// <see cref="OrchestratorResultsAzuriteTests"/>: a skip looks like a pass.
/// </summary>
public class OrchestratorRetentionAzuriteTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required OrchestratorTableStore Store { get; init; }
        public required AzureTableStore Backing { get; init; }
        public required CraftSettings Settings { get; init; }
        public required string Connection { get; init; }

        public static async Task<Fixture?> TryConnectAsync()
        {
            var settings = new CraftSettings();
            var connection = Environment.GetEnvironmentVariable("CRAFT_TEST_TABLE_CONNECTION");
            if (!string.IsNullOrWhiteSpace(connection))
                settings.Auth.UserStorageConnection = connection;
            else
            {
                settings.Storage.AllowDevelopmentStorage = true;
                connection = "UseDevelopmentStorage=true";
            }

            // Unique per run so repeated runs cannot see each other's rows.
            settings.Orchestrator.TablePrefix = "azrt" + Guid.NewGuid().ToString("N")[..8];

            var backing = new AzureTableStore(settings);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await backing.PingAsync(cts.Token);
            }
            catch
            {
                return null;
            }

            var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
            await store.InitializeAsync();
            return new Fixture { Store = store, Backing = backing, Settings = settings, Connection = connection };
        }

        public string Table(string suffix) => Settings.Orchestrator.TablePrefix + suffix;

        public async Task<int> CountAsync(string suffix, string partition)
        {
            var n = 0;
            await foreach (var _ in Backing.QueryPartitionAsync(Table(suffix), partition)) n++;
            return n;
        }

        public Task AddRunAsync(string name, string status, DateTime started, DateTime? completed) =>
            Store.UpsertRunAsync(new OrchestratorRun { Name = name, Status = status, StartedUtc = started, CompletedUtc = completed });

        public Task AddTasksAsync(string run, int count) =>
            Store.UpsertTaskBatchAsync(run, Enumerable.Range(1, count)
                .Select(i => new OrchestratorTaskItem { Id = $"task-{i}", Status = "Completed" }).ToList());

        public async ValueTask DisposeAsync()
        {
            // Drop the per-run tables so repeated local runs do not pile fixtures into the emulator.
            var service = new TableServiceClient(Connection);
            foreach (var suffix in new[] { "Runs", "Tasks", "Results" })
            {
                try { await service.DeleteTableAsync(Table(suffix)); }
                catch (RequestFailedException) { /* already gone */ }
            }
        }
    }

    [Fact]
    public async Task Sweep_RemovesFinishedRunsPastRetention_AndKeepsEverythingStillWanted()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        var old = DateTime.UtcNow.AddDays(-3);
        var recent = DateTime.UtcNow.AddHours(-1);

        await fx.AddRunAsync("done-old", "Completed", old, old);
        await fx.AddTasksAsync("done-old", 3);
        await fx.Store.StoreResultAsync("done-old", "task-1", "{\"ok\":true}");

        await fx.AddRunAsync("cancelled-old", "Cancelled", old, old);
        await fx.AddTasksAsync("cancelled-old", 1);

        await fx.AddRunAsync("failed-recent", "Failed", recent, recent);
        await fx.AddTasksAsync("failed-recent", 2);

        // Started long ago, nobody in this process drives it, but its counter row was written just
        // now — the heartbeat that says another process (or this one, moments ago) is still at it.
        await fx.AddRunAsync("running", "Running", DateTime.UtcNow.AddDays(-10), null);
        await fx.AddTasksAsync("running", 2);
        await fx.Store.InitRemainingAsync("running", 2);

        // No Run row, but written moments ago: an orphan, just not an old one.
        await fx.AddTasksAsync("ghost", 2);

        var result = await fx.Store.CleanupOldRunsAsync(TimeSpan.FromHours(48), new HashSet<string>());

        Assert.Collection(result.ExpiredRuns.OrderBy(n => n, StringComparer.Ordinal),
            n => Assert.Equal("cancelled-old", n),
            n => Assert.Equal("done-old", n));
        Assert.Empty(result.AbandonedRuns);
        Assert.Equal(0, result.OrphanPartitionsRemoved);
        Assert.Equal(4, result.RunsExamined);

        Assert.Null(await fx.Store.GetRunAsync("done-old"));
        Assert.Null(await fx.Store.GetRunAsync("cancelled-old"));
        Assert.Equal(0, await fx.CountAsync("Tasks", "done-old"));
        Assert.Equal(0, await fx.CountAsync("Results", "done-old"));
        Assert.Equal(0, await fx.CountAsync("Tasks", "cancelled-old"));

        Assert.NotNull(await fx.Store.GetRunAsync("failed-recent"));
        Assert.Equal(2, await fx.CountAsync("Tasks", "failed-recent"));
        Assert.Equal(2, (await fx.Store.GetRunAsync("running"))!.Tasks.Count);
        Assert.Equal(2, await fx.CountAsync("Tasks", "ghost"));
    }

    [Fact]
    public async Task Sweep_RemovesOrphanedPartitions_OnceNothingInThemIsRecent()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        await fx.AddTasksAsync("ghost", 3);
        await fx.Store.StoreResultAsync("ghost", "task-1", "{\"ok\":true}");

        // A run this process is driving: exempt from the abandoned rule, and its partition is never
        // an orphan because its Run row is there.
        await fx.AddRunAsync("kept", "Running", DateTime.UtcNow, null);
        await fx.AddTasksAsync("kept", 2);

        // Every row was stamped by the service a moment ago. A cutoff in the future is the only way
        // to make them "old" without waiting, and it also absorbs any skew between the emulator's
        // clock and this process's.
        var result = await fx.Store.CleanupOldRunsAsync(TimeSpan.FromMinutes(-5), new HashSet<string> { "kept" });

        Assert.Equal(2, result.OrphanPartitionsRemoved);
        Assert.Empty(result.ExpiredRuns);
        Assert.Empty(result.AbandonedRuns);
        Assert.Equal(0, await fx.CountAsync("Tasks", "ghost"));
        Assert.Equal(0, await fx.CountAsync("Results", "ghost"));
        Assert.Equal(2, await fx.CountAsync("Tasks", "kept"));
        Assert.NotNull(await fx.Store.GetRunAsync("kept"));
    }
}
