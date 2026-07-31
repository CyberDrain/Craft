using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Craft.Configuration;
using Craft.Orchestration;

namespace Craft.Storage;

/// <summary>
/// Typed CRUD wrapper over <see cref="ICraftTableStore"/> for orchestrator persistence. Manages three
/// logical tables: {prefix}Runs, {prefix}Tasks, {prefix}Results. Persists through the
/// <see cref="ICraftTableStore"/> abstraction.
/// </summary>
public class OrchestratorTableStore
{
    private readonly ILogger<OrchestratorTableStore> _logger;
    private readonly ICraftTableStore _store;
    private readonly string _runsTable;
    private readonly string _tasksTable;
    private readonly string _resultsTable;
    private bool _initialized;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OrchestratorTableStore(ILogger<OrchestratorTableStore> logger, CraftSettings settings, ICraftTableStore store)
    {
        _logger = logger;
        _store = store;
        var prefix = settings.Orchestrator.TablePrefix;
        _runsTable = $"{prefix}Runs";
        _tasksTable = $"{prefix}Tasks";
        _resultsTable = $"{prefix}Results";
    }

    /// <summary>Create the three tables if they do not exist. Called once on startup.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _store.EnsureTableAsync(_runsTable);
        await _store.EnsureTableAsync(_tasksTable);
        await _store.EnsureTableAsync(_resultsTable);
        _initialized = true;

        _logger.LogInformation("[OrchestratorStore] Tables initialized");
    }

    /// <summary>Upsert run metadata (without tasks — tasks are separate rows).</summary>
    public async Task UpsertRunAsync(OrchestratorRun run)
    {
        var row = new StoreRow("Run", run.Name)
        {
            Properties =
            {
                ["Status"] = run.Status,
                ["Priority"] = run.Priority,
                ["StartedUtc"] = run.StartedUtc,
                ["CompletedUtc"] = run.CompletedUtc,
                ["TaskScriptName"] = run.TaskScriptName,
                ["PostExecFunctionName"] = run.PostExecFunctionName,
                ["PostExecParametersJson"] = run.PostExecParametersJson,
                ["PostExecStatus"] = run.PostExecStatus,
                ["Reference"] = run.Reference,
                ["ParentRunName"] = run.ParentRunName,
                ["TaskCount"] = run.Tasks.Count
            }
        };

        await _store.UpsertAsync(_runsTable, row);
    }

    /// <summary>Load a run and all its tasks. Returns null if the run does not exist.</summary>
    public async Task<OrchestratorRun?> GetRunAsync(string name)
    {
        var runRow = await _store.GetAsync(_runsTable, "Run", name);
        if (runRow == null) return null;

        var run = new OrchestratorRun
        {
            Name = name,
            Status = runRow.GetString("Status") ?? "Pending",
            Priority = runRow.GetInt32("Priority") ?? 2,
            StartedUtc = runRow.GetDateTimeOffset("StartedUtc")?.UtcDateTime ?? DateTime.UtcNow,
            CompletedUtc = runRow.GetDateTimeOffset("CompletedUtc")?.UtcDateTime,
            TaskScriptName = runRow.GetString("TaskScriptName"),
            PostExecFunctionName = runRow.GetString("PostExecFunctionName"),
            PostExecParametersJson = runRow.GetString("PostExecParametersJson"),
            PostExecStatus = runRow.GetString("PostExecStatus"),
            // Both were in-memory only until now: a resumed run came back with a null Reference
            // (so FindRunByReference could not see it) and a null ParentRunName (so its finalize
            // never re-checked the parent). Absent on rows written before this existed.
            Reference = runRow.GetString("Reference"),
            ParentRunName = runRow.GetString("ParentRunName")
        };

        var tasks = new List<OrchestratorTaskItem>();
        await foreach (var taskRow in _store.QueryPartitionAsync(_tasksTable, name))
        {
            var parametersJson = taskRow.GetString("ParametersJson");
            Dictionary<string, object> parameters;
            try
            {
                parameters = !string.IsNullOrEmpty(parametersJson)
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson, s_jsonOptions) ?? []
                    : [];
            }
            catch
            {
                parameters = [];
            }

            tasks.Add(new OrchestratorTaskItem
            {
                Id = taskRow.RowKey,
                Status = taskRow.GetString("Status") ?? "Pending",
                Parameters = parameters,
                AttemptCount = taskRow.GetInt32("AttemptCount") ?? 0,
                LastError = taskRow.GetString("LastError"),
                // Absent on rows written before per-task priority existed — null means "inherit the run's".
                Priority = taskRow.GetInt32("Priority"),
                CompletedUtc = taskRow.GetDateTimeOffset("CompletedUtc")?.UtcDateTime
            });
        }

        run.Tasks = tasks;
        return run;
    }

    /// <summary>List all known run names.</summary>
    public async Task<List<string>> ListRunsAsync()
    {
        var names = new List<string>();
        await foreach (var row in _store.QueryPartitionAsync(_runsTable, "Run"))
            names.Add(row.RowKey);
        return names;
    }

    /// <summary>
    /// List every run's identity and parentage without loading its tasks.
    ///
    /// One scan of the single "Run" partition. <see cref="GetRunAsync"/> would answer the same question
    /// but does a task-partition query per run, so using it to rebuild parent/child links at startup
    /// would load every task of every run — the opposite of what recovery should cost.
    /// </summary>
    public async Task<List<OrchestratorRunSummary>> ListRunSummariesAsync()
    {
        var summaries = new List<OrchestratorRunSummary>();
        await foreach (var row in _store.QueryPartitionAsync(_runsTable, "Run"))
        {
            summaries.Add(new OrchestratorRunSummary(
                row.RowKey,
                row.GetString("Status") ?? "Pending",
                row.GetString("ParentRunName")));
        }
        return summaries;
    }

    /// <summary>Upsert a single task row.</summary>
    public Task UpsertTaskAsync(string runName, OrchestratorTaskItem task) =>
        _store.UpsertAsync(_tasksTable, BuildTaskRow(runName, task));

    /// <summary>Batch upsert all tasks for a run (used at run creation).</summary>
    public Task UpsertTaskBatchAsync(string runName, List<OrchestratorTaskItem> tasks) =>
        _store.UpsertBatchAsync(_tasksTable, runName, tasks.Select(t => BuildTaskRow(runName, t)).ToList());

    /// <summary>
    /// Write a set of coalesced task-status transitions. Rows are grouped by run (partition) and handed
    /// to the store, which applies each group as atomically as the backend allows and chunks to any
    /// per-request limits. Used by the batched status writer; the large-result path
    /// (<see cref="StoreResultAsync"/>) is untouched.
    /// </summary>
    /// <returns>
    /// The run names whose writes did NOT persist. Callers must retry these rather than dropping them —
    /// they are terminal task states, and losing one means a finished task looks Pending forever.
    /// An empty list means everything landed.
    /// </returns>
    public async Task<IReadOnlyList<string>> WriteTaskStatusBatchAsync(IReadOnlyList<TaskStatusWrite> writes,
        int maxConcurrency = 8, CancellationToken ct = default)
    {
        // Grouped by run because a batch has to share a partition key. Run these with bounded
        // concurrency instead of one after another: the workload that broke this was ~600 runs of a
        // single task each, so "batching" degenerated into hundreds of sequential round-trips inside
        // one flush — with the single drain loop, and therefore every task waiting on its durable
        // marker, blocked for the whole duration.
        var groups = writes.GroupBy(w => w.RunName).ToList();
        if (groups.Count == 0) return [];

        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = groups.Select(async group =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await _store.UpsertBatchAsync(_tasksTable, group.Key, group.Select(BuildTaskRow).ToList(), ct);
            }
            catch (Exception ex)
            {
                // One run's failure must not discard the other 599. Record it and carry on; the caller
                // requeues just this run.
                failed.Add(group.Key);
                _logger.LogWarning(ex, "[OrchestratorStore] Task status write failed for run {Run} ({Count} tasks) — will retry",
                    group.Key, group.Count());
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return failed.ToList();
    }

    private static StoreRow BuildTaskRow(string runName, OrchestratorTaskItem task) => new(runName, task.Id)
    {
        Properties =
        {
            ["Status"] = task.Status,
            ["ParametersJson"] = JsonSerializer.Serialize(task.Parameters, s_jsonOptions),
            ["AttemptCount"] = task.AttemptCount,
            ["LastError"] = task.LastError,
            ["Priority"] = task.Priority,
            ["CompletedUtc"] = task.CompletedUtc.HasValue
                ? new DateTimeOffset(task.CompletedUtc.Value, TimeSpan.Zero)
                : (DateTimeOffset?)null
        }
    };

    private static StoreRow BuildTaskRow(TaskStatusWrite w) => new(w.RunName, w.TaskId)
    {
        Properties =
        {
            ["Status"] = w.Status,
            ["ParametersJson"] = w.ParametersJson,
            ["AttemptCount"] = w.AttemptCount,
            ["LastError"] = w.LastError,
            ["Priority"] = w.Priority,
            ["CompletedUtc"] = w.CompletedUtc.HasValue
                ? new DateTimeOffset(w.CompletedUtc.Value, TimeSpan.Zero)
                : (DateTimeOffset?)null
        }
    };

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
    /// <see cref="StreamResultsToFileAsync"/> for run-sized payloads.
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
    /// results on the fly. Writes a JSON array.
    ///
    /// Chunks are written individually, so the largest allocation this makes is one chunk
    /// (<see cref="MaxPropertyChars"/>) — the reassembled result is never built as a string.
    /// </summary>
    public async Task StreamResultsToFileAsync(string runName, string filePath, CancellationToken ct = default)
    {
        var count = 0;

        await using (var writer = new StreamWriter(filePath, append: false, Encoding.UTF8, bufferSize: 65536))
        {
            await writer.WriteAsync('[');
            var first = true;

            await foreach (var chunks in StreamResultChunkGroupsAsync(runName, ct))
            {
                if (!first) await writer.WriteAsync(',');
                foreach (var chunk in chunks)
                    await writer.WriteAsync(chunk);
                first = false;
                count++;
            }

            await writer.WriteAsync(']');
        }

        _logger.LogInformation("[OrchestratorStore] Streamed {Count} results to {Path} for run {Name}",
            count, filePath, runName);
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

    /// <summary>Delete all entities across the 3 tables for a completed run.</summary>
    public async Task CleanupRunAsync(string runName)
    {
        try
        {
            await _store.DeleteAsync(_runsTable, "Run", runName);
            await _store.DeletePartitionAsync(_tasksTable, runName);
            await _store.DeletePartitionAsync(_resultsTable, runName);

            _logger.LogInformation("[OrchestratorStore] Cleaned up run: {Name}", runName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrchestratorStore] Failed to cleanup run: {Name}", runName);
        }
    }

    /// <summary>Delete all runs (and their tasks/results) older than the retention period.</summary>
    public async Task CleanupOldRunsAsync(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;

        // Collect first, then delete — avoids mutating the "Run" partition while enumerating it.
        var toClean = new List<string>();
        await foreach (var row in _store.QueryPartitionAsync(_runsTable, "Run"))
        {
            var completedUtc = row.GetDateTimeOffset("CompletedUtc");
            var status = row.GetString("Status");

            if (status is "Completed" or "CompletedWithErrors" or "Failed"
                && completedUtc.HasValue && completedUtc.Value < cutoff)
            {
                toClean.Add(row.RowKey);
            }
        }

        foreach (var name in toClean)
            await CleanupRunAsync(name);

        if (toClean.Count > 0)
            _logger.LogInformation("[OrchestratorStore] Cleaned up {Count} old runs (retention: {Days}d)",
                toClean.Count, retention.TotalDays);
    }

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
