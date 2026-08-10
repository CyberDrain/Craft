using System.Runtime.CompilerServices;
using System.Text.Json;
using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The orchestrator results path is the largest single allocation the host makes: for a 738-task run the
/// aggregate JSON is 50-150MB against a 2398MB heap cap (DOTNET_GCHeapHardLimit in build/Dockerfile),
/// and post-execution can run for several runs at once.
///
/// It used to call LoadResultGroupsAsync first, which materialized EVERY result row into a dictionary
/// before writing a byte — so the methods named "stream" held the entire payload, as UTF-16, before they
/// started. These tests pin the streaming behaviour so that cannot come back, and pin the reassembly
/// semantics (single-property, multi-chunk-single-row, and multi-row spill) that the rewrite had to
/// preserve.
/// </summary>
public class OrchestratorResultStreamingTests
{
    /// <summary>
    /// An in-memory store that reports how many rows it has actually yielded, so a consumer can be
    /// checked for laziness rather than just for its final answer.
    /// </summary>
    private sealed class LazyProbeStore : ICraftTableStore
    {

        // Claims are not exercised by this fake. Fail loudly rather than pretend the guard held —
        // a silent 'true' here would look exactly like a successful claim.
        public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default) => throw new NotSupportedException();
        private readonly Dictionary<string, Dictionary<(string, string), StoreRow>> _tables = new();

        /// <summary>Rows handed out by <see cref="QueryPartitionAsync"/> so far.</summary>
        public int RowsYielded;

        /// <summary>Rows to emit before the shuffle point, used to force out-of-order arrival.</summary>
        public Func<IEnumerable<StoreRow>, IEnumerable<StoreRow>> Order { get; set; } = rows => rows;

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureTableAsync(string table, CancellationToken ct = default)
        {
            if (!_tables.ContainsKey(table)) _tables[table] = new();
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            _tables[table][(row.PartitionKey, row.RowKey)] = row;
            return Task.CompletedTask;
        }

        public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default)
        {
            foreach (var r in rows) _tables[table][(r.PartitionKey, r.RowKey)] = r;
            return Task.CompletedTask;
        }

        public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
            => Task.FromResult(_tables[table].TryGetValue((partitionKey, rowKey), out var r) ? r : null);

        public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var rows = _tables[table].Where(k => k.Key.Item1 == partitionKey).Select(k => k.Value).ToList();
            foreach (var row in Order(rows))
            {
                Interlocked.Increment(ref RowsYielded);
                yield return row;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _tables[table].ToList())
            {
                yield return kv.Value;
                await Task.Yield();
            }
        }

        public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
        {
            _tables[table].Remove((partitionKey, rowKey));
            return Task.CompletedTask;
        }

        public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
        {
            foreach (var k in _tables[table].Keys.Where(k => k.Item1 == partitionKey).ToList())
                _tables[table].Remove(k);
            return Task.CompletedTask;
        }
    }

    private static (OrchestratorTableStore Store, LazyProbeStore Backing) NewStore()
    {
        var backing = new LazyProbeStore();
        var store = new OrchestratorTableStore(
            NullLogger<OrchestratorTableStore>.Instance, new CraftSettings(), backing);
        return (store, backing);
    }

    /// <summary>A result whose JSON exceeds the per-property limit and so gets chunked.</summary>
    private static string BigJson(int chars) => "\"" + new string('x', chars - 2) + "\"";

    // ── Laziness: the regression guard ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamResults_DoesNotDrainTheStore_BeforeYieldingTheFirstResult()
    {
        var (store, backing) = NewStore();
        await store.InitializeAsync();
        for (var i = 0; i < 200; i++)
            await store.StoreResultAsync("run", $"task{i:D3}", $$"""{"tenant":"t{{i}}"}""");

        backing.RowsYielded = 0;

        await using var e = store.StreamResultsAsync("run").GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());

        // One result out ⇒ at most one row consumed. The old implementation read all 200 first.
        Assert.True(backing.RowsYielded <= 1,
            $"{backing.RowsYielded} of 200 rows were read to produce the FIRST result — " +
            "the results path is materializing the run before writing");
    }

    [Fact]
    public async Task StreamResults_ConsumesRowsIncrementally_AcrossTheWholeRun()
    {
        var (store, backing) = NewStore();
        await store.InitializeAsync();
        for (var i = 0; i < 200; i++)
            await store.StoreResultAsync("run", $"task{i:D3}", $$"""{"tenant":"t{{i}}"}""");

        backing.RowsYielded = 0;
        var produced = 0;
        var maxRowsAhead = 0;

        await foreach (var _ in store.StreamResultsAsync("run"))
        {
            produced++;
            maxRowsAhead = Math.Max(maxRowsAhead, backing.RowsYielded - produced);
        }

        Assert.Equal(200, produced);
        Assert.True(maxRowsAhead <= 1,
            $"the reader ran up to {maxRowsAhead} rows ahead of what it emitted — expected lock-step streaming");
    }

    [Fact]
    public async Task StreamResultsToJsonLines_WritesWhileReading_NotAfterDrainingTheRun()
    {
        var (store, backing) = NewStore();
        await store.InitializeAsync();

        // Results big enough that the aggregate far exceeds the StreamWriter's 64KB buffer, so a
        // genuinely streaming writer must have flushed bytes to disk before the last row is read.
        const int Results = 150;
        for (var i = 0; i < Results; i++)
            await store.StoreResultAsync("run", $"task{i:D3}", BigJson(4_000));

        var path = Path.Combine(Path.GetTempPath(), $"craft-test-{Guid.NewGuid():N}.jsonl");
        long fileLengthAtLastRow = -1;

        backing.RowsYielded = 0;
        backing.Order = rows => rows.Select(r =>
        {
            // Sample the file the moment the FINAL row is handed out. Streaming ⇒ earlier results are
            // already on disk. Buffering ⇒ nothing has been written yet, because writing has not begun.
            if (backing.RowsYielded == Results - 1)
                fileLengthAtLastRow = File.Exists(path) ? new FileInfo(path).Length : 0;
            return r;
        });

        try
        {
            var count = await store.StreamResultsToJsonLinesAsync("run", path);

            Assert.Equal(Results, count);
            Assert.Equal(Results, ReadJsonLines(path).Count);
            Assert.Equal(Results, backing.RowsYielded);
            Assert.True(fileLengthAtLastRow > 0,
                "nothing had been written to the file by the time the last row was read — " +
                "the results path drains the run before it starts writing");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Reassembly semantics the rewrite had to preserve ────────────────────────────────────────────

    [Fact]
    public async Task SingleProperty_MultiChunkSingleRow_AndMultiRowSpill_AllRoundTrip()
    {
        var (store, _) = NewStore();
        await store.InitializeAsync();

        var small = """{"kind":"small"}""";                 // one property
        var chunked = BigJson(90_000);                       // >30k chars → chunked, still one row
        var spilled = BigJson(1_200_000);                    // >450k chars → spills across rows

        await store.StoreResultAsync("run", "a-small", small);
        await store.StoreResultAsync("run", "b-chunked", chunked);
        await store.StoreResultAsync("run", "c-spilled", spilled);

        var results = await store.GetResultsAsync("run");

        Assert.Equal(3, results.Length);
        Assert.Contains(small, results);
        Assert.Contains(chunked, results);
        Assert.Contains(spilled, results);
    }

    [Fact]
    public async Task SpilledResult_Reassembles_WhenRowsArriveOutOfOrder()
    {
        var (store, backing) = NewStore();
        await store.InitializeAsync();

        var spilled = BigJson(1_200_000);
        await store.StoreResultAsync("run", "spilled", spilled);
        await store.StoreResultAsync("run", "small", """{"kind":"small"}""");

        // ICraftTableStore.QueryPartitionAsync promises no ordering — reverse it and the grouping must
        // still complete by chunk count rather than by arrival order.
        backing.Order = rows => rows.Reverse();

        var results = await store.GetResultsAsync("run");

        Assert.Equal(2, results.Length);
        Assert.Contains(spilled, results);
    }

    // ── JSON Lines: one result per line, whatever the result's storage shape ────────────────────────

    /// <summary>Read the file the way Invoke-CraftPostExecution does — line by line, parsing each.</summary>
    private static List<JsonElement> ReadJsonLines(string path) =>
        File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l))
            .ToList();

    [Fact]
    public async Task EachResult_IsExactlyOneLine_ForAMixOfResultShapes()
    {
        var (store, _) = NewStore();
        await store.InitializeAsync();

        // One property, chunked-within-a-row, and spilled-across-rows. The reader must not be able to
        // tell which is which: a result's storage shape is not allowed to change its line count.
        await store.StoreResultAsync("run", "a", """{"i":1}""");
        await store.StoreResultAsync("run", "b", BigJson(90_000));
        await store.StoreResultAsync("run", "c", BigJson(1_200_000));
        await store.StoreResultAsync("run", "d", """{"i":4}""");

        var path = Path.Combine(Path.GetTempPath(), $"craft-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var count = await store.StreamResultsToJsonLinesAsync("run", path);

            Assert.Equal(4, count);
            Assert.Equal(4, File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)));
            Assert.Equal(4, ReadJsonLines(path).Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ResultContainingRawNewlines_StillOccupiesOneLine()
    {
        var (store, _) = NewStore();
        await store.InitializeAsync();

        // A task script that wrote several objects to the output stream gets them joined with "\n"
        // by the runner. That must not become several lines here, or one broken result would be
        // silently read back as several results.
        await store.StoreResultAsync("run", "a", "{\"i\":1}");
        await store.StoreResultAsync("run", "b", "{\"x\":1}\n{\"x\":2}\r\n{\"x\":3}");
        await store.StoreResultAsync("run", "c", "{\"i\":3}");

        var path = Path.Combine(Path.GetTempPath(), $"craft-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var count = await store.StreamResultsToJsonLinesAsync("run", path);

            Assert.Equal(3, count);
            Assert.Equal(3, File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RawNewlinesSpanningAChunkBoundary_StillOccupyOneLine()
    {
        var (store, _) = NewStore();
        await store.InitializeAsync();

        // The writer emits chunk by chunk, so a newline landing at a chunk edge is the case a
        // per-chunk scan could miss. Build a result long enough to chunk with newlines throughout.
        var noisy = string.Concat(Enumerable.Repeat("{\"x\":1}\n", 20_000));
        await store.StoreResultAsync("run", "noisy", noisy);

        var path = Path.Combine(Path.GetTempPath(), $"craft-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var count = await store.StreamResultsToJsonLinesAsync("run", path);

            Assert.Equal(1, count);
            Assert.Equal(1, File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)));

            // And nothing was lost but the newlines themselves.
            Assert.Equal(noisy.Replace("\n", ""), File.ReadAllText(path).TrimEnd('\n'));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyRun_WritesAnEmptyFile()
    {
        var (store, _) = NewStore();
        await store.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"craft-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var count = await store.StreamResultsToJsonLinesAsync("run", path);

            // Empty, not "[]" — JSON Lines has no enclosing token, and the reader's loop over zero
            // lines yields zero results without needing to special-case it.
            Assert.Equal(0, count);
            Assert.Equal("", await File.ReadAllTextAsync(path));
            Assert.Empty(await store.GetResultsAsync("run"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
