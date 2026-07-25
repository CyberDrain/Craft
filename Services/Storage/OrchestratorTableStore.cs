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
            PostExecStatus = runRow.GetString("PostExecStatus")
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
    public async Task WriteTaskStatusBatchAsync(IReadOnlyList<TaskStatusWrite> writes)
    {
        foreach (var group in writes.GroupBy(w => w.RunName))
            await _store.UpsertBatchAsync(_tasksTable, group.Key, group.Select(BuildTaskRow).ToList());
    }

    private static StoreRow BuildTaskRow(string runName, OrchestratorTaskItem task) => new(runName, task.Id)
    {
        Properties =
        {
            ["Status"] = task.Status,
            ["ParametersJson"] = JsonSerializer.Serialize(task.Parameters, s_jsonOptions),
            ["AttemptCount"] = task.AttemptCount,
            ["LastError"] = task.LastError,
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

    /// <summary>Get all result JSON strings for a run, reassembling any chunked/multi-row results.</summary>
    public async Task<string[]> GetResultsAsync(string runName)
    {
        var grouped = await LoadResultGroupsAsync(runName);

        var results = new List<string>();
        foreach (var (_, rows) in grouped)
        {
            var sorted = rows.OrderBy(e => e.GetInt32("PartIndex") ?? 0).ToList();
            var totalChunks = sorted.Select(e => e.GetInt32("ResultChunkCount")).FirstOrDefault(c => c.HasValue) ?? 0;

            if (totalChunks == 0)
            {
                var json = sorted[0].GetString("ResultJson");
                if (!string.IsNullOrEmpty(json))
                    results.Add(json);
                continue;
            }

            var sb = new StringBuilder();
            AppendChunks(sb, sorted, totalChunks);
            if (sb.Length > 0)
                results.Add(sb.ToString());
        }

        return results.ToArray();
    }

    /// <summary>
    /// Stream all result JSON strings for a run directly to a file, reassembling any chunked/multi-row
    /// results on the fly. Avoids loading all results into one in-memory string. Writes a JSON array.
    /// </summary>
    public async Task StreamResultsToFileAsync(string runName, string filePath)
    {
        var grouped = await LoadResultGroupsAsync(runName);

        await using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8, bufferSize: 65536);
        await writer.WriteAsync('[');
        var first = true;

        foreach (var (_, rows) in grouped)
        {
            var sorted = rows.OrderBy(e => e.GetInt32("PartIndex") ?? 0).ToList();
            var totalChunks = sorted.Select(e => e.GetInt32("ResultChunkCount")).FirstOrDefault(c => c.HasValue) ?? 0;

            if (totalChunks == 0)
            {
                var json = sorted[0].GetString("ResultJson");
                if (!string.IsNullOrEmpty(json))
                {
                    if (!first) await writer.WriteAsync(',');
                    await writer.WriteAsync(json);
                    first = false;
                }
                continue;
            }

            if (!first) await writer.WriteAsync(',');
            for (int i = 0; i < totalChunks; i++)
            {
                var propName = $"ResultJson_{i}";
                foreach (var row in sorted)
                {
                    var chunk = row.GetString(propName);
                    if (chunk != null) { await writer.WriteAsync(chunk); break; }
                }
            }
            first = false;
        }

        await writer.WriteAsync(']');

        _logger.LogInformation("[OrchestratorStore] Streamed {Count} results to {Path} for run {Name}",
            grouped.Count, filePath, runName);
    }

    /// <summary>Load all result rows for a run, grouped by logical result (root row + any spill rows).</summary>
    private async Task<Dictionary<string, List<StoreRow>>> LoadResultGroupsAsync(string runName)
    {
        var grouped = new Dictionary<string, List<StoreRow>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var row in _store.QueryPartitionAsync(_resultsTable, runName))
        {
            var originalId = row.GetString("OriginalEntityId");
            var key = !string.IsNullOrEmpty(originalId) ? originalId : row.RowKey;
            if (!grouped.TryGetValue(key, out var group))
            {
                group = new List<StoreRow>();
                grouped[key] = group;
            }
            group.Add(row);
        }
        return grouped;
    }

    private static void AppendChunks(StringBuilder sb, List<StoreRow> sorted, int totalChunks)
    {
        for (int i = 0; i < totalChunks; i++)
        {
            var propName = $"ResultJson_{i}";
            foreach (var row in sorted)
            {
                var chunk = row.GetString(propName);
                if (chunk != null) { sb.Append(chunk); break; }
            }
        }
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
