using Azure;
using Azure.Data.Tables;
using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Tests here allocate multi-MB strings to force the storage size limits, and heap-delta measurement
/// tests (see <see cref="RetentionMeasurement"/>) cannot share a process with concurrent allocation.
/// Marking this collection non-parallel puts it in the same sequential phase as those, so the two never
/// run at once. See the note on <see cref="RetentionMeasurement"/> for the failure mode this avoids.
/// </summary>
[CollectionDefinition(LargeAllocationSerialTests.Name, DisableParallelization = true)]
public class LargeAllocationSerialTests
{
    public const string Name = "large-allocation-serial";
}

/// <summary>
/// Proves the large-entity split/reassemble path against a real table backend: a value too big for one
/// property (or one entity) is stored transparently and read back byte-identical, claims still work on a
/// split row, shrinking a split entity does not resurrect it from stale parts, and deleting one takes its
/// parts with it. Azurite by default, a real account via CRAFT_TEST_TABLE_CONNECTION; skipped, not failed,
/// when neither is reachable (a skip reads as a pass).
///
/// This is the regression guard for the failure it was written for: a scheduled task whose Parameters
/// embed a whole policy template exceeded Azure Table's 64 KiB-per-property limit, so the orchestrator
/// task row 400'd with PropertyValueTooLarge, was dropped, and the task could never be dispatched.
/// </summary>
[Collection(LargeAllocationSerialTests.Name)]
public class AzureTableStoreLargeEntityTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required AzureTableStore Store { get; init; }
        public required string Table { get; init; }
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

            var table = "azle" + Guid.NewGuid().ToString("N")[..8];
            await store.EnsureTableAsync(table);
            return new Fixture { Store = store, Table = table, Connection = connection };
        }

        /// <summary>Count the physical rows in a partition, bypassing reassembly (raw client).</summary>
        public async Task<int> PhysicalRowCountAsync(string partitionKey)
        {
            var count = 0;
            var client = new TableClient(Connection, Table);
            await foreach (var _ in client.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'"))
                count++;
            return count;
        }

        public async ValueTask DisposeAsync()
        {
            try { await new TableServiceClient(Connection).DeleteTableAsync(Table); }
            catch (RequestFailedException) { /* never created, or already gone */ }
        }
    }

    private static string Text(int chars, char c = 'x') => new(c, chars);

    private static StoreRow Row(string pk, string rk, string bigValue) =>
        new(pk, rk) { Properties = { ["Status"] = "Pending", ["ParametersJson"] = bigValue } };

    [Fact]
    public async Task ColumnSplit_OversizedProperty_RoundTripsThroughUpsertAndGetAndQuery()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        var value = Text(80_000); // > 64 KiB per-property limit; this is the exact failure mode
        await fx.Store.UpsertAsync(fx.Table, Row("p", "task1", value));

        var got = await fx.Store.GetAsync(fx.Table, "p", "task1");
        Assert.Equal(value, got?.GetString("ParametersJson"));
        Assert.Equal("Pending", got?.GetString("Status"));

        var queried = new List<StoreRow>();
        await foreach (var r in fx.Store.QueryPartitionAsync(fx.Table, "p")) queried.Add(r);
        Assert.Single(queried);
        Assert.Equal(value, queried[0].GetString("ParametersJson"));

        // A column split stays one physical row.
        Assert.Equal(1, await fx.PhysicalRowCountAsync("p"));
    }

    [Fact]
    public async Task CrossRowSplit_HugeProperty_RoundTripsAndUsesMultipleRows()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        var value = Text(1_200_000); // over the 1 MiB entity cap → spills onto extra rows
        await fx.Store.UpsertAsync(fx.Table, Row("p", "task1", value));

        Assert.True(await fx.PhysicalRowCountAsync("p") > 1, "expected the entity to occupy more than one physical row");

        var got = await fx.Store.GetAsync(fx.Table, "p", "task1");
        Assert.Equal(value, got?.GetString("ParametersJson"));

        var queried = new List<StoreRow>();
        await foreach (var r in fx.Store.QueryPartitionAsync(fx.Table, "p")) queried.Add(r);
        Assert.Single(queried);
        Assert.Equal(value, queried[0].GetString("ParametersJson"));
    }

    [Fact]
    public async Task UpsertBatch_MixesLargeAndSmallRows_AllRoundTrip()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        var big = Text(1_200_000);
        var mid = Text(80_000);
        await fx.Store.UpsertBatchAsync(fx.Table, "p", new[]
        {
            new StoreRow("p", "small") { Properties = { ["ParametersJson"] = "tiny" } },
            Row("p", "mid", mid),
            Row("p", "big", big),
        });

        var byKey = new Dictionary<string, string?>();
        await foreach (var r in fx.Store.QueryPartitionAsync(fx.Table, "p"))
            byKey[r.RowKey] = r.GetString("ParametersJson");

        Assert.Equal(3, byKey.Count);
        Assert.Equal("tiny", byKey["small"]);
        Assert.Equal(mid, byKey["mid"]);
        Assert.Equal(big, byKey["big"]);
    }

    [Fact]
    public async Task ShrinkingASplitEntityToASmallValue_ReadsTheNewValue_WithoutResurrection()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        await fx.Store.UpsertAsync(fx.Table, Row("p", "task1", Text(1_200_000)));   // large: many rows
        await fx.Store.UpsertAsync(fx.Table, Row("p", "task1", "small-now"));        // small: one plain row

        // The correctness guarantee: both read paths return the new small value, never the old one
        // reassembled from leftover part rows (plain-row precedence). Physical cleanup of those leftovers
        // happens on the next ENGAGED write or on delete, not on a shrink-to-small — matching the
        // AzBobbyTables write path, which would otherwise add a scan to every small upsert.
        var got = await fx.Store.GetAsync(fx.Table, "p", "task1");
        Assert.Equal("small-now", got?.GetString("ParametersJson"));

        var queried = new List<StoreRow>();
        await foreach (var r in fx.Store.QueryPartitionAsync(fx.Table, "p")) queried.Add(r);
        Assert.Single(queried);
        Assert.Equal("small-now", queried[0].GetString("ParametersJson"));
    }

    [Fact]
    public async Task EngagedOverwrite_ReclaimsStalePartRows_FromTheLargerVersion()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        await fx.Store.UpsertAsync(fx.Table, Row("p", "task1", Text(3_000_000)));  // many rows
        var smaller = Row("p", "task1", Text(1_100_000));                          // still split, fewer rows
        await fx.Store.UpsertAsync(fx.Table, smaller);

        var got = await fx.Store.GetAsync(fx.Table, "p", "task1");
        Assert.Equal(Text(1_100_000), got?.GetString("ParametersJson"));

        // The engaged write removed the higher-index part rows the larger version had left behind:
        // exactly the new version's rows remain.
        var expected = EntitySplitter.Split(new TableEntity("p", "task1")
        {
            ["Status"] = "Pending",
            ["ParametersJson"] = Text(1_100_000)
        }).Rows.Count;
        Assert.Equal(expected, await fx.PhysicalRowCountAsync("p"));
    }

    [Fact]
    public async Task ConditionalReplace_WorksOnALargeChunkedRow()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        await fx.Store.UpsertAsync(fx.Table, Row("p", "task1", Text(80_000)));

        var current = await fx.Store.GetAsync(fx.Table, "p", "task1");
        Assert.NotNull(current);
        current!["Status"] = "Running";

        Assert.True(await fx.Store.TryReplaceBatchAsync(fx.Table, "p", new[] { current }));
        Assert.Equal("Running", (await fx.Store.GetAsync(fx.Table, "p", "task1"))?.GetString("Status"));

        // The now-stale ETag must be rejected — no silent unconditional overwrite.
        current["Status"] = "Cancelled";
        Assert.False(await fx.Store.TryReplaceBatchAsync(fx.Table, "p", new[] { current }));
    }

    [Fact]
    public async Task Delete_RemovesEveryPartRow_OfASplitEntity()
    {
        await using var fx = await Fixture.TryConnectAsync();
        if (fx == null) return;

        await fx.Store.UpsertAsync(fx.Table, Row("p", "task1", Text(1_200_000)));
        Assert.True(await fx.PhysicalRowCountAsync("p") > 1);

        await fx.Store.DeleteAsync(fx.Table, "p", "task1");

        Assert.Equal(0, await fx.PhysicalRowCountAsync("p"));
        Assert.Null(await fx.Store.GetAsync(fx.Table, "p", "task1"));
        var any = false;
        await foreach (var _ in fx.Store.QueryPartitionAsync(fx.Table, "p")) any = true;
        Assert.False(any);
    }
}
