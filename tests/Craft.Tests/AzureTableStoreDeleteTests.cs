using Azure.Data.Tables;
using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// A delete transaction holding one row that is already gone fails whole, so a bulk delete that swallowed the
/// 404 left every other row of that batch behind for good. The real service's behaviour is pinned here, then the
/// store's bulk deletes are held to "a missing row is skipped, the rest still go". Azurite by default, a real
/// account via CRAFT_TEST_TABLE_CONNECTION; skipped when neither is reachable.
/// </summary>
public class AzureTableStoreDeleteTests
{
    private static readonly string[] ABC = ["a", "b", "c"];
    private static readonly string[] KeyColumns = ["PartitionKey", "RowKey", "Timestamp"];

    private static async Task<(AzureTableStore Store, TableClient Client)?> ConnectAsync()
    {
        var settings = new CraftSettings();
        var connection = Environment.GetEnvironmentVariable("CRAFT_TEST_TABLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection)) settings.Auth.UserStorageConnection = connection;
        else
        {
            settings.Storage.AllowDevelopmentStorage = true;
            connection = "UseDevelopmentStorage=true";
        }
        var store = new AzureTableStore(settings, NullLogger<AzureTableStore>.Instance);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await store.PingAsync(cts.Token);
        }
        catch
        {
            return null;
        }
        var table = "azdl" + Guid.NewGuid().ToString("N")[..8];
        await store.EnsureTableAsync(table);
        return (store, new TableClient(connection, table));
    }

    private static async Task SeedAsync(TableClient client, IEnumerable<string> keys)
    {
        foreach (var chunk in keys.Chunk(100))
            await client.SubmitTransactionAsync(chunk.Select(k => new TableTransactionAction(TableTransactionActionType.Add, new TableEntity("p", k))));
    }

    private static async Task<List<string>> KeysAsync(TableClient client)
    {
        var keys = new List<string>();
        await foreach (var e in client.QueryAsync<TableEntity>("PartitionKey eq 'p'", select: ["RowKey"])) keys.Add(e.RowKey);
        return keys;
    }

    [Fact]
    public async Task TheService_FailsAWholeDeleteTransaction_OverOneMissingRow()
    {
        if (await ConnectAsync() is not { } c) return;
        try
        {
            await SeedAsync(c.Client, ["a", "c"]);

            var ex = await Assert.ThrowsAsync<TableTransactionFailedException>(() => c.Client.SubmitTransactionAsync(
                ABC.Select(k => new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity("p", k)))));

            Assert.Equal(404, ex.Status);
            Assert.Equal("ResourceNotFound", ex.ErrorCode);
            Assert.Equal(1, ex.FailedTransactionActionIndex);
            Assert.Equal(["a", "c"], await KeysAsync(c.Client));
        }
        finally { await c.Client.DeleteAsync(); }
    }

    [Fact]
    public async Task DeleteBatch_WithRowsAlreadyGone_StillDeletesEveryOtherRow()
    {
        if (await ConnectAsync() is not { } c) return;
        try
        {
            var keys = Enumerable.Range(0, 150).Select(i => $"r{i:D3}").ToList();
            await SeedAsync(c.Client, keys.Where(k => k is not "r010" and not "r120"));

            await c.Store.DeleteBatchAsync(c.Client.Name, "p", keys);

            Assert.Empty(await KeysAsync(c.Client));
        }
        finally { await c.Client.DeleteAsync(); }
    }

    /// <summary>The orphan sweep ages partitions by Timestamp from a keys-only scan; without it nothing would ever go.</summary>
    [Fact]
    public async Task TheKeysOnlyScan_ReturnsEachRowsTimestamp()
    {
        if (await ConnectAsync() is not { } c) return;
        try
        {
            await SeedAsync(c.Client, ["a", "b"]);

            var rows = new List<StoreRow>();
            await foreach (var r in c.Store.QueryTableAsync(c.Client.Name, null, KeyColumns)) rows.Add(r);

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.InRange(r.Timestamp!.Value, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)));
        }
        finally { await c.Client.DeleteAsync(); }
    }

    [Fact]
    public async Task DeletePartition_RemovesEveryRow_AcrossBatches()
    {
        if (await ConnectAsync() is not { } c) return;
        try
        {
            await SeedAsync(c.Client, Enumerable.Range(0, 250).Select(i => $"r{i:D3}"));

            await c.Store.DeletePartitionAsync(c.Client.Name, "p");

            Assert.Empty(await KeysAsync(c.Client));
        }
        finally { await c.Client.DeleteAsync(); }
    }
}
