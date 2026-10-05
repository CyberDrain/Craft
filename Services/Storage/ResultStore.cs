using System.Runtime.CompilerServices;
using System.Text;
using Craft.Configuration;

namespace Craft.Storage;

/// <summary>
/// Task results for a run's aggregation, one partition per run (the run key). A result is written before
/// its task is counted done, so the aggregation never runs short of one that finished.
/// </summary>
public sealed class ResultStore
{
    private readonly ILogger<ResultStore> _logger;
    private readonly ICraftTableStore _store;
    private readonly string _resultsTable;

    public ResultStore(ILogger<ResultStore> logger, CraftSettings settings, ICraftTableStore store)
    {
        _logger = logger;
        _store = store;
        _resultsTable = $"{settings.Orchestrator.TablePrefix}TaskResults";
    }

    public Task InitializeAsync(CancellationToken ct = default) => _store.EnsureTableAsync(_resultsTable, ct);

    // ─── Result storage ───
    // Results can be large (50–150 MB for big runs). We chunk a result across multiple properties and,
    // if needed, multiple rows in the same partition. These bounds are sized for Azure Table Storage
    // (64 KiB/property, 1 MiB/entity); on a backend without those limits the chunking is simply
    // unnecessary but still correct, and it keeps per-row payloads small (good for e.g. SQL packet size).
    private const int MaxPropertyChars = 30_000;
    private const int MaxEntityChars = 450_000;

    /// <summary>Store a single task result, chunking large JSON across properties/rows as needed.</summary>
    public async Task StoreResultAsync(string runName, string taskId, string resultJson)
    {
        // Fast path: fits in a single property
        if (resultJson.Length <= MaxPropertyChars)
        {
            var row = new StoreRow(runName, taskId) { Properties = { ["ResultJson"] = resultJson } };
            await _store.UpsertAsync(_resultsTable, row);
            return;
        }

        var chunks = ChunkString(resultJson, MaxPropertyChars);

        // Try to fit all chunks into a single row
        if (EstimateTotalChars(chunks) <= MaxEntityChars)
        {
            var row = new StoreRow(runName, taskId);
            for (int i = 0; i < chunks.Count; i++)
                row[$"ResultJson_{i}"] = chunks[i];
            row["ResultChunkCount"] = chunks.Count;

            await _store.UpsertAsync(_resultsTable, row);
            return;
        }

        // Row too large — split across multiple rows
        var rowIndex = 0;
        var chunkIndex = 0;

        while (chunkIndex < chunks.Count)
        {
            var rowKey = rowIndex == 0 ? taskId : $"{taskId}-part{rowIndex}";
            var row = new StoreRow(runName, rowKey);

            if (rowIndex > 0)
            {
                row["OriginalEntityId"] = taskId;
                row["PartIndex"] = rowIndex;
            }

            var currentChars = runName.Length + rowKey.Length + 100; // overhead estimate

            while (chunkIndex < chunks.Count)
            {
                var chunkChars = chunks[chunkIndex].Length;
                if (currentChars + chunkChars + 20 > MaxEntityChars)
                    break;

                row[$"ResultJson_{chunkIndex}"] = chunks[chunkIndex];
                currentChars += chunkChars + 20;
                chunkIndex++;
            }

            row["ResultChunkCount"] = chunks.Count;
            await _store.UpsertAsync(_resultsTable, row);
            rowIndex++;
        }
    }

    /// <summary>
    /// Get all result JSON strings for a run, reassembling any chunked/multi-row results.
    ///
    /// Buffers every result by signature — prefer <see cref="StreamResultsAsync"/> or
    /// <see cref="StreamResultsToJsonLinesAsync"/> for run-sized payloads.
    /// </summary>
    public async Task<string[]> GetResultsAsync(string runName, CancellationToken ct = default)
    {
        var results = new List<string>();
        await foreach (var result in StreamResultsAsync(runName, ct))
            results.Add(result);
        return results.ToArray();
    }

    /// <summary>
    /// Stream each run result, reassembled, as it becomes available from the backing store.
    ///
    /// Nothing is buffered except spill groups still waiting for their remaining rows, so a run whose
    /// results each fit in one row holds ONE row at a time regardless of how many there are.
    /// </summary>
    public async IAsyncEnumerable<string> StreamResultsAsync(string runName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunks in StreamResultChunkGroupsAsync(runName, ct))
            yield return chunks.Count == 1 ? chunks[0] : string.Concat(chunks);
    }

    /// <summary>
    /// Stream all result JSON strings for a run directly to a file, reassembling any chunked/multi-row
    /// results on the fly. Writes JSON Lines (NDJSON): one result per line, no enclosing array.
    ///
    /// Chunks are written individually, so the largest allocation this makes is one chunk
    /// (<see cref="MaxPropertyChars"/>) — the reassembled result is never built as a string.
    ///
    /// JSON Lines rather than a JSON array because the consumer is PowerShell. A JSON array forces the
    /// reader to hold the whole document to find where each element ends; one result per line lets
    /// Invoke-CraftPostExecution walk the file with File.ReadLines and hold ONE result at a time. That
    /// is the difference between a 50-150MB Large Object Heap allocation per post-execution and none.
    /// It also isolates failure: a malformed result costs that result, not the entire aggregate.
    ///
    /// Returns the number of results written.
    /// </summary>
    public async Task<int> StreamResultsToJsonLinesAsync(string runName, string filePath,
        CancellationToken ct = default)
    {
        var count = 0;

        await using (var writer = new StreamWriter(filePath, append: false, Encoding.UTF8, bufferSize: 65536))
        {
            await foreach (var chunks in StreamResultChunkGroupsAsync(runName, ct))
            {
                foreach (var chunk in chunks)
                    await WriteSingleLineAsync(writer, chunk);
                await writer.WriteAsync('\n');
                count++;
            }
        }

        _logger.LogInformation("[OrchestratorStore] Streamed {Count} results to {Path} for run {Name}",
            count, filePath, runName);

        return count;
    }

    /// <summary>
    /// Write a chunk with any raw CR/LF removed, so one result stays on one line.
    ///
    /// Results are expected to be compact JSON on a single line — that is what Invoke-CraftTask's
    /// `ConvertTo-Json -Compress` produces, and JSON escapes newlines inside strings as \n rather than
    /// emitting them raw. A raw newline can therefore only appear as inter-token whitespace, which
    /// carries no meaning, or in a result that was not valid JSON to begin with (a task script that
    /// wrote several objects to the output stream — the runner joins those with "\n"). Dropping the
    /// character is right in the first case and no worse than today's behaviour in the second, where
    /// the malformed result currently takes the whole aggregate's parse down with it.
    ///
    /// The scan is the common-case fast path: no newline means the chunk is written untouched, with
    /// no copy and no per-character work beyond the search itself.
    /// </summary>
    private static async Task WriteSingleLineAsync(StreamWriter writer, string chunk)
    {
        var start = 0;
        int idx;

        while ((idx = chunk.AsSpan(start).IndexOfAny('\r', '\n')) >= 0)
        {
            var abs = start + idx;
            if (abs > start)
                await writer.WriteAsync(chunk.AsMemory(start, abs - start));
            start = abs + 1;
        }

        if (start == 0)
            await writer.WriteAsync(chunk);
        else if (start < chunk.Length)
            await writer.WriteAsync(chunk.AsMemory(start));
    }

    /// <summary>
    /// The shared core: yields each logical result as its ordered chunk list, as soon as that result is
    /// complete, and drops every row it has finished with.
    ///
    /// This used to be LoadResultGroupsAsync, which materialized EVERY result row for the run into a
    /// dictionary before a single byte was written — so the callers named "stream" held the entire
    /// payload (as UTF-16, ~2x the stored size) before they started. For a 738-task run whose aggregate
    /// is 50-150MB that was a few hundred MB against a 2398MB heap cap, concurrently per post-execution.
    ///
    /// Rows are grouped by (OriginalEntityId ?? RowKey) and completed by chunk count rather than by
    /// arrival order, so this makes no assumption about the order
    /// <see cref="ICraftTableStore.QueryPartitionAsync"/> returns rows in — the interface promises none.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<string>> StreamResultChunkGroupsAsync(string runName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Allocated only if this run actually has a result too large for a single row.
        Dictionary<string, PendingResult>? pending = null;

        await foreach (var row in _store.QueryPartitionAsync(_resultsTable, runName, ct))
        {
            var totalChunks = row.GetInt32("ResultChunkCount") ?? 0;

            // Fast path: the whole result is one property on this row. Emit and release it.
            if (totalChunks == 0)
            {
                var json = row.GetString("ResultJson");
                if (!string.IsNullOrEmpty(json)) yield return new[] { json };
                continue;
            }

            var originalId = row.GetString("OriginalEntityId");
            var key = !string.IsNullOrEmpty(originalId) ? originalId : row.RowKey;

            pending ??= new Dictionary<string, PendingResult>(StringComparer.OrdinalIgnoreCase);
            if (!pending.TryGetValue(key, out var group))
                pending[key] = group = new PendingResult(totalChunks);

            group.Absorb(row);

            // Chunked but single-row results complete on their first (only) row.
            if (group.IsComplete)
            {
                pending.Remove(key);
                if (group.HasContent) yield return group.Chunks;
            }
        }

        // A spill row never arrived (partial write, or cleanup raced us). Emit what we have rather than
        // silently dropping the result, and say so.
        if (pending is { Count: > 0 })
        {
            foreach (var (key, group) in pending)
            {
                _logger.LogWarning(
                    "[OrchestratorStore] Result {Key} in run {Run} is incomplete: {Have}/{Total} chunks present",
                    key, runName, group.PresentCount, group.TotalChunks);
                if (group.HasContent) yield return group.Chunks;
            }
        }
    }

    /// <summary>
    /// A result being reassembled from chunks spread over one or more rows. Holds only this result's
    /// chunks — never the <see cref="StoreRow"/>s they came from.
    /// </summary>
    private sealed class PendingResult(int totalChunks)
    {
        private readonly string[] _chunks = new string[totalChunks];

        public int TotalChunks => _chunks.Length;
        public int PresentCount { get; private set; }
        public bool IsComplete => PresentCount == _chunks.Length;
        public bool HasContent => _chunks.Any(c => !string.IsNullOrEmpty(c));

        /// <summary>Take any chunks this row carries that we do not already have.</summary>
        public void Absorb(StoreRow row)
        {
            for (var i = 0; i < _chunks.Length; i++)
            {
                if (_chunks[i] != null) continue;
                var chunk = row.GetString($"ResultJson_{i}");
                if (chunk == null) continue;
                _chunks[i] = chunk;
                PresentCount++;
            }
        }

        /// <summary>The chunks in index order. Missing chunks (incomplete result) are skipped.</summary>
        public IReadOnlyList<string> Chunks =>
            PresentCount == _chunks.Length ? _chunks : _chunks.Where(c => c != null).ToArray();
    }

    /// <summary>Drop a run's results once its aggregation has read them.</summary>
    public Task DeleteRunAsync(string runKey, CancellationToken ct = default) =>
        _store.DeletePartitionAsync(_resultsTable, runKey, ct);

    /// <summary>Split a string into chunks of at most maxChars characters, avoiding surrogate splits.</summary>
    internal static List<string> ChunkString(string value, int maxChars)
    {
        var chunks = new List<string>();
        var start = 0;

        while (start < value.Length)
        {
            var remaining = value.Length - start;
            var take = Math.Min(remaining, maxChars);

            if (take < remaining && char.IsHighSurrogate(value[start + take - 1]))
                take--;

            chunks.Add(value.Substring(start, take));
            start += take;
        }

        return chunks;
    }

    private static int EstimateTotalChars(List<string> chunks)
    {
        var total = 200; // overhead for keys + metadata properties
        for (int i = 0; i < chunks.Count; i++)
            total += chunks[i].Length + 20; // chunk + property name
        return total;
    }
}
