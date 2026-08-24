using Azure;
using Azure.Data.Tables;
using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging;

namespace Craft.Tests;

/// <summary>
/// A table that is missing when the host needs it — never created, or deleted through table maintenance
/// or a reset that cleared the orchestrator's state — must come back on the next operation that notices,
/// and that operation must still do its job. Only the real backend can prove this: the error code that
/// tells a missing table from a missing row is the service's, and the batch path's silent fallback is
/// exactly what a fake never reproduces.
///
/// The fast tests use a table that was never created, which answers 404 TableNotFound at once on both
/// Azurite and the real service. A table that was just DELETED is different on the real service: for
/// about a minute it still accepts every operation and then discards them with the table, while create
/// answers 409 TableBeingDeleted — so the store can only react once the 404 finally arrives, and must
/// then wait the window out. That costs a minute per test against a real account, so it is covered by
/// one test gated on CRAFT_TEST_INCLUDE_SLOW=1. Azurite by default, a real account via
/// CRAFT_TEST_TABLE_CONNECTION; skipped, not failed, when neither is reachable — a skip looks like a pass.
/// </summary>
public class AzureTableStoreRecreateTests
{
    /// <summary>Captures the store's log lines; the recreate path announces itself with one.</summary>
    private sealed class ListLogger(List<string> sink) : ILogger<AzureTableStore>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink) sink.Add(formatter(state, exception));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required AzureTableStore Store { get; init; }
        public required string Table { get; init; }
        public required string Connection { get; init; }
        public required List<string> Logs { get; init; }

        public bool Recreated
        {
            get { lock (Logs) return Logs.Any(m => m.Contains("has been recreated", StringComparison.Ordinal)); }
        }

        /// <param name="create">Whether the table exists before the test starts.</param>
        public static async Task<Fixture?> TryConnectAsync(bool create)
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

            var logs = new List<string>();
            var store = new AzureTableStore(settings, new ListLogger(logs));
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await store.PingAsync(cts.Token);
            }
            catch
            {
                return null;
            }

            var table = "azrc" + Guid.NewGuid().ToString("N")[..8];
            if (create) await store.EnsureTableAsync(table);
            return new Fixture { Store = store, Table = table, Connection = connection, Logs = logs };
        }

        /// <summary>Delete the table behind the store's back, the way table maintenance would.</summary>
        public async Task DropTableAsync() => await new TableServiceClient(Connection).DeleteTableAsync(Table);

        public async Task<bool> TableExistsAsync()
        {
            await foreach (var _ in new TableServiceClient(Connection).QueryAsync($"TableName eq '{Table}'"))
                return true;
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            try { await new TableServiceClient(Connection).DeleteTableAsync(Table); }
            catch (RequestFailedException) { /* never created, or already gone */ }
        }
    }

    private static StoreRow Row(string rowKey) => new("p", rowKey) { Properties = { ["Value"] = rowKey } };

    private static bool SlowTestsEnabled =>
        Environment.GetEnvironmentVariable("CRAFT_TEST_INCLUDE_SLOW") == "1";

    [Fact]
    public async Task Upsert_IntoAMissingTable_CreatesIt_AndTheRowLands()
    {
        await using var fx = await Fixture.TryConnectAsync(create: false);
        if (fx == null) return;

        await fx.Store.UpsertAsync(fx.Table, Row("one"));

        Assert.True(fx.Recreated);
        var back = await fx.Store.GetAsync(fx.Table, "p", "one");
        Assert.Equal("one", back?.GetString("Value"));
    }

    [Fact]
    public async Task UpsertBatch_IntoAMissingTable_CreatesIt_AndEveryRowLands()
    {
        // This path used to lose the batch without a trace: the transaction failed, the per-entity
        // fallback failed the same way, and nothing was logged or thrown.
        await using var fx = await Fixture.TryConnectAsync(create: false);
        if (fx == null) return;

        await fx.Store.UpsertBatchAsync(fx.Table, "p", [Row("a"), Row("b"), Row("c")]);

        Assert.True(fx.Recreated);
        var keys = new List<string>();
        await foreach (var row in fx.Store.QueryPartitionAsync(fx.Table, "p")) keys.Add(row.RowKey);
        Assert.Equal("a,b,c", string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Query_OnAMissingTable_CreatesIt_AndYieldsNothing()
    {
        await using var fx = await Fixture.TryConnectAsync(create: false);
        if (fx == null) return;

        var count = 0;
        await foreach (var _ in fx.Store.QueryTableAsync(fx.Table)) count++;

        Assert.Equal(0, count);
        Assert.True(fx.Recreated);
        Assert.True(await fx.TableExistsAsync());
    }

    [Fact]
    public async Task Get_OnAMissingTable_IsNull_AndCreatesIt()
    {
        await using var fx = await Fixture.TryConnectAsync(create: false);
        if (fx == null) return;

        Assert.Null(await fx.Store.GetAsync(fx.Table, "p", "x"));
        Assert.True(fx.Recreated);
        Assert.True(await fx.TableExistsAsync());
    }

    [Fact]
    public async Task Get_OnAMissingRow_IsStillJustNull()
    {
        // The routine 404 keeps its routine answer — only the table-level one triggers a create.
        await using var fx = await Fixture.TryConnectAsync(create: true);
        if (fx == null) return;

        Assert.Null(await fx.Store.GetAsync(fx.Table, "p", "never-written"));
        Assert.False(fx.Recreated);
    }

    [Fact]
    public async Task DeletePartition_OnAMissingTable_DoesNotThrow()
    {
        await using var fx = await Fixture.TryConnectAsync(create: false);
        if (fx == null) return;

        await fx.Store.DeletePartitionAsync(fx.Table, "p");

        Assert.True(fx.Recreated);
        Assert.True(await fx.TableExistsAsync());
    }

    [Fact]
    public async Task Upsert_AfterTheTableWasJustDeleted_WaitsOutTheDeletionWindow_AndLands()
    {
        // Slow on the real service (about a minute): the doomed table keeps answering until the service
        // finally reports it gone, and create is refused with TableBeingDeleted until the window ends.
        // The store has to ride out both. Azurite has no window, so there the first write recreates.
        if (!SlowTestsEnabled) return;
        await using var fx = await Fixture.TryConnectAsync(create: true);
        if (fx == null) return;
        await fx.DropTableAsync();

        // Writes the service accepts into the doomed table are lost with it — the service's behaviour,
        // and why this keeps writing until the store reports that it saw the 404 and recreated the table.
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!fx.Recreated && DateTime.UtcNow < deadline)
        {
            await fx.Store.UpsertAsync(fx.Table, Row("survivor"));
            if (!fx.Recreated) await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.True(fx.Recreated, "The store never saw the table go missing within three minutes of its deletion.");
        Assert.Equal("survivor", (await fx.Store.GetAsync(fx.Table, "p", "survivor"))?.GetString("Value"));
    }
}
