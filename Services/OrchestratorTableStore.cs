using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace Craft.Services;

/// <summary>
/// Typed CRUD wrapper around Azure Table Storage for orchestrator persistence.
/// Manages three tables: CippOrchestratorRuns, CippOrchestratorTasks, CippOrchestratorResults.
/// Replaces the local-file SaveRun/LoadRun and in-memory OrchestrationResults.
/// </summary>
public class OrchestratorTableStore
{
    private readonly ILogger<OrchestratorTableStore> _logger;
    private readonly TableClient _runsTable;
    private readonly TableClient _tasksTable;
    private readonly TableClient _resultsTable;
    private bool _initialized;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OrchestratorTableStore(ILogger<OrchestratorTableStore> logger, CraftSettings settings)
    {
        _logger = logger;
        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                               ?? "UseDevelopmentStorage=true";

        var prefix = settings.Orchestrator.TablePrefix;
        _runsTable = new TableClient(connectionString, $"{prefix}Runs");
        _tasksTable = new TableClient(connectionString, $"{prefix}Tasks");
        _resultsTable = new TableClient(connectionString, $"{prefix}Results");
    }

    /// <summary>
    /// Create the three tables if they do not exist. Called once on startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _runsTable.CreateIfNotExistsAsync();
        await _tasksTable.CreateIfNotExistsAsync();
        await _resultsTable.CreateIfNotExistsAsync();
        _initialized = true;

        _logger.LogInformation("[OrchestratorStore] Tables initialized");
    }

    /// <summary>
    /// Upsert run metadata (without tasks — tasks are separate rows).
    /// </summary>
    public async Task UpsertRunAsync(OrchestratorRun run)
    {
        var entity = new TableEntity("Run", run.Name)
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
        };

        await _runsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    /// <summary>
    /// Load a run and all its tasks from both tables.
    /// Returns null if the run does not exist.
    /// </summary>
    public async Task<OrchestratorRun?> GetRunAsync(string name)
    {
        try
        {
            var response = await _runsTable.GetEntityAsync<TableEntity>("Run", name);
            var entity = response.Value;

            var run = new OrchestratorRun
            {
                Name = name,
                Status = entity.GetString("Status") ?? "Pending",
                Priority = entity.GetInt32("Priority") ?? 2,
                StartedUtc = entity.GetDateTimeOffset("StartedUtc")?.UtcDateTime ?? DateTime.UtcNow,
                CompletedUtc = entity.GetDateTimeOffset("CompletedUtc")?.UtcDateTime,
                TaskScriptName = entity.GetString("TaskScriptName"),
                PostExecFunctionName = entity.GetString("PostExecFunctionName"),
                PostExecParametersJson = entity.GetString("PostExecParametersJson"),
                PostExecStatus = entity.GetString("PostExecStatus")
            };

            // Load tasks
            var tasks = new List<OrchestratorTaskItem>();
            await foreach (var taskEntity in _tasksTable.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{EscapeFilter(name)}'"))
            {
                var parametersJson = taskEntity.GetString("ParametersJson");
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
                    Id = taskEntity.RowKey,
                    Status = taskEntity.GetString("Status") ?? "Pending",
                    Parameters = parameters,
                    AttemptCount = taskEntity.GetInt32("AttemptCount") ?? 0,
                    LastError = taskEntity.GetString("LastError"),
                    CompletedUtc = taskEntity.GetDateTimeOffset("CompletedUtc")?.UtcDateTime
                });
            }

            run.Tasks = tasks;
            return run;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// List all known run names.
    /// </summary>
    public async Task<List<string>> ListRunsAsync()
    {
        var names = new List<string>();
        await foreach (var entity in _runsTable.QueryAsync<TableEntity>(
            filter: "PartitionKey eq 'Run'",
            select: new[] { "RowKey" }))
        {
            names.Add(entity.RowKey);
        }
        return names;
    }

    /// <summary>
    /// Upsert a single task row.
    /// </summary>
    public async Task UpsertTaskAsync(string runName, OrchestratorTaskItem task)
    {
        var entity = new TableEntity(runName, task.Id)
        {
            ["Status"] = task.Status,
            ["ParametersJson"] = JsonSerializer.Serialize(task.Parameters, s_jsonOptions),
            ["AttemptCount"] = task.AttemptCount,
            ["LastError"] = task.LastError,
            ["CompletedUtc"] = task.CompletedUtc.HasValue
                ? new DateTimeOffset(task.CompletedUtc.Value, TimeSpan.Zero)
                : (DateTimeOffset?)null
        };

        await _tasksTable.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    /// <summary>
    /// Batch upsert all tasks for a run (used at run creation).
    /// Azure Table batch operations are limited to 100 entities per batch
    /// and all entities in a batch must share the same PartitionKey.
    /// </summary>
    public async Task UpsertTaskBatchAsync(string runName, List<OrchestratorTaskItem> tasks)
    {
        const int batchSize = 100;

        for (int i = 0; i < tasks.Count; i += batchSize)
        {
            var batch = new List<TableTransactionAction>();
            var chunk = tasks.Skip(i).Take(batchSize);

            foreach (var task in chunk)
            {
                var entity = new TableEntity(runName, task.Id)
                {
                    ["Status"] = task.Status,
                    ["ParametersJson"] = JsonSerializer.Serialize(task.Parameters, s_jsonOptions),
                    ["AttemptCount"] = task.AttemptCount,
                    ["LastError"] = task.LastError,
                    ["CompletedUtc"] = task.CompletedUtc.HasValue
                        ? new DateTimeOffset(task.CompletedUtc.Value, TimeSpan.Zero)
                        : (DateTimeOffset?)null
                };

                batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, entity));
            }

            await _tasksTable.SubmitTransactionAsync(batch);
        }
    }

    // ─── Constants for Azure Table Storage limits ───
    // Azure Table string properties are UTF-16 encoded, max 64 KiB (≈32K chars).
    // We use 30,000 chars to stay safely under the limit with multi-byte chars.
    private const int MaxPropertyChars = 30_000;
    // Max entity size is 1 MiB. We use ~900KB (UTF-16 basis) as our ceiling.
    // In practice, at 30K chars/property, ~15 properties would hit 1 MiB,
    // well under the 252 custom property limit.
    private const int MaxEntityChars = 450_000; // ~900KB UTF-16

    /// <summary>
    /// Store a single task result, automatically chunking large JSON across
    /// properties and rows to stay within Azure Table Storage limits.
    ///
    /// Azure Table Storage limits:
    ///   - String property: 64 KiB UTF-16 (~32K chars)
    ///   - Entity total: 1 MiB
    ///   - Max 252 custom properties per entity
    ///
    /// Chunking strategy (mirrors CIPP's Add-CIPPAzDataTableEntity):
    ///   1. If ResultJson fits in one property (&lt;30K chars) → single entity, single property
    ///   2. If ResultJson is large → split into ResultJson_0, _1, etc. (each &lt;30K chars)
    ///   3. If the entity total exceeds ~450K chars → spill across rows with OriginalEntityId + PartIndex
    /// </summary>
    public async Task StoreResultAsync(string runName, string taskId, string resultJson)
    {
        // Fast path: fits in a single property
        if (resultJson.Length <= MaxPropertyChars)
        {
            var entity = new TableEntity(runName, taskId)
            {
                ["ResultJson"] = resultJson
            };
            await _resultsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            return;
        }

        // Split the JSON string into chunks that fit within property size limits
        var chunks = ChunkString(resultJson, MaxPropertyChars);

        // Try to fit all chunks into a single entity
        if (EstimateTotalChars(chunks) <= MaxEntityChars)
        {
            var entity = new TableEntity(runName, taskId);
            for (int i = 0; i < chunks.Count; i++)
                entity[$"ResultJson_{i}"] = chunks[i];
            entity["ResultChunkCount"] = chunks.Count;

            await _resultsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            return;
        }

        // Entity too large — split across multiple rows
        var rowIndex = 0;
        var chunkIndex = 0;

        while (chunkIndex < chunks.Count)
        {
            var rowKey = rowIndex == 0 ? taskId : $"{taskId}-part{rowIndex}";
            var entity = new TableEntity(runName, rowKey);

            if (rowIndex > 0)
            {
                entity["OriginalEntityId"] = taskId;
                entity["PartIndex"] = rowIndex;
            }

            // Pack as many chunks as fit into this row
            var currentChars = runName.Length + rowKey.Length + 100; // overhead estimate

            while (chunkIndex < chunks.Count)
            {
                var chunkChars = chunks[chunkIndex].Length;
                // Property name length (e.g. "ResultJson_99" = ~15 chars)
                if (currentChars + chunkChars + 20 > MaxEntityChars)
                    break;

                entity[$"ResultJson_{chunkIndex}"] = chunks[chunkIndex];
                currentChars += chunkChars + 20;
                chunkIndex++;
            }

            entity["ResultChunkCount"] = chunks.Count;
            await _resultsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            rowIndex++;
        }
    }

    /// <summary>
    /// Get all result JSON strings for a run, reassembling any chunked/multi-row results.
    /// </summary>
    public async Task<string[]> GetResultsAsync(string runName)
    {
        // Load all result entities for this run
        var allEntities = new List<TableEntity>();
        await foreach (var entity in _resultsTable.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeFilter(runName)}'"))
        {
            allEntities.Add(entity);
        }

        // Group: standalone rows vs parts of a multi-row result
        var standalone = new Dictionary<string, List<TableEntity>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in allEntities)
        {
            var originalId = entity.GetString("OriginalEntityId");
            var key = !string.IsNullOrEmpty(originalId) ? originalId : entity.RowKey;

            if (!standalone.TryGetValue(key, out var group))
            {
                group = new List<TableEntity>();
                standalone[key] = group;
            }
            group.Add(entity);
        }

        var results = new List<string>();
        foreach (var (_, entities) in standalone)
        {
            // Sort by PartIndex (root row has no PartIndex → treat as 0)
            var sorted = entities
                .OrderBy(e => e.GetInt32("PartIndex") ?? 0)
                .ToList();

            // Determine total chunk count from any row (all rows carry it)
            var totalChunks = sorted
                .Select(e => e.GetInt32("ResultChunkCount"))
                .FirstOrDefault(c => c.HasValue) ?? 0;

            if (totalChunks == 0)
            {
                // Simple unchunked result
                var json = sorted[0].GetString("ResultJson");
                if (!string.IsNullOrEmpty(json))
                    results.Add(json);
                continue;
            }

            // Reassemble chunks across all rows
            var sb = new StringBuilder();
            for (int i = 0; i < totalChunks; i++)
            {
                var propName = $"ResultJson_{i}";
                foreach (var entity in sorted)
                {
                    var chunk = entity.GetString(propName);
                    if (chunk != null)
                    {
                        sb.Append(chunk);
                        break;
                    }
                }
            }

            if (sb.Length > 0)
                results.Add(sb.ToString());
        }

        return results.ToArray();
    }

    /// <summary>
    /// Stream all result JSON strings for a run directly to a file, reassembling
    /// any chunked/multi-row results on the fly. Avoids loading all results into
    /// a single in-memory string (which can be 50-150 MB for large runs).
    /// Writes a JSON array: [{result1},{result2},...]
    /// </summary>
    public async Task StreamResultsToFileAsync(string runName, string filePath)
    {
        // Load all result entities for this run
        var allEntities = new List<TableEntity>();
        await foreach (var entity in _resultsTable.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeFilter(runName)}'"))
        {
            allEntities.Add(entity);
        }

        // Group: standalone rows vs parts of a multi-row result
        var standalone = new Dictionary<string, List<TableEntity>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in allEntities)
        {
            var originalId = entity.GetString("OriginalEntityId");
            var key = !string.IsNullOrEmpty(originalId) ? originalId : entity.RowKey;

            if (!standalone.TryGetValue(key, out var group))
            {
                group = new List<TableEntity>();
                standalone[key] = group;
            }
            group.Add(entity);
        }

        await using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8, bufferSize: 65536);
        await writer.WriteAsync('[');
        var first = true;

        foreach (var (_, entities) in standalone)
        {
            var sorted = entities
                .OrderBy(e => e.GetInt32("PartIndex") ?? 0)
                .ToList();

            var totalChunks = sorted
                .Select(e => e.GetInt32("ResultChunkCount"))
                .FirstOrDefault(c => c.HasValue) ?? 0;

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

            // Reassemble chunks and write directly to file
            if (!first) await writer.WriteAsync(',');
            for (int i = 0; i < totalChunks; i++)
            {
                var propName = $"ResultJson_{i}";
                foreach (var entity in sorted)
                {
                    var chunk = entity.GetString(propName);
                    if (chunk != null)
                    {
                        await writer.WriteAsync(chunk);
                        break;
                    }
                }
            }
            first = false;
        }

        await writer.WriteAsync(']');

        _logger.LogInformation("[OrchestratorStore] Streamed {Count} results to {Path} for run {Name}",
            standalone.Count, filePath, runName);
    }

    /// <summary>
    /// Delete all entities across the 3 tables for a completed run.
    /// Called after PostExec completes or after retention period expires.
    /// </summary>
    public async Task CleanupRunAsync(string runName)
    {
        try
        {
            // Delete run metadata
            try
            {
                await _runsTable.DeleteEntityAsync("Run", runName);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete all tasks
            await DeletePartitionAsync(_tasksTable, runName);

            // Delete all results
            await DeletePartitionAsync(_resultsTable, runName);

            _logger.LogInformation("[OrchestratorStore] Cleaned up run: {Name}", runName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrchestratorStore] Failed to cleanup run: {Name}", runName);
        }
    }

    /// <summary>
    /// Delete all runs (and their tasks/results) older than the specified retention period.
    /// </summary>
    public async Task CleanupOldRunsAsync(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;
        var deletedCount = 0;

        await foreach (var entity in _runsTable.QueryAsync<TableEntity>(
            filter: "PartitionKey eq 'Run'",
            select: new[] { "RowKey", "CompletedUtc", "Status" }))
        {
            var completedUtc = entity.GetDateTimeOffset("CompletedUtc");
            var status = entity.GetString("Status");

            // Only cleanup completed/failed runs that are past retention
            if (status is "Completed" or "CompletedWithErrors" or "Failed"
                && completedUtc.HasValue && completedUtc.Value < cutoff)
            {
                await CleanupRunAsync(entity.RowKey);
                deletedCount++;
            }
        }

        if (deletedCount > 0)
            _logger.LogInformation("[OrchestratorStore] Cleaned up {Count} old runs (retention: {Days}d)",
                deletedCount, retention.TotalDays);
    }

    private async Task DeletePartitionAsync(TableClient table, string partitionKey)
    {
        var toDelete = new List<TableEntity>();
        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeFilter(partitionKey)}'",
            select: new[] { "PartitionKey", "RowKey" }))
        {
            toDelete.Add(entity);
        }

        // Batch delete in groups of 100
        const int batchSize = 100;
        for (int i = 0; i < toDelete.Count; i += batchSize)
        {
            var batch = toDelete.Skip(i).Take(batchSize)
                .Select(e => new TableTransactionAction(TableTransactionActionType.Delete, e))
                .ToList();
            try
            {
                await table.SubmitTransactionAsync(batch);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted — safe to ignore
            }
        }
    }

    /// <summary>
    /// Split a string into chunks of at most maxChars characters.
    /// Avoids splitting surrogate pairs.
    /// </summary>
    internal static List<string> ChunkString(string value, int maxChars)
    {
        var chunks = new List<string>();
        var start = 0;

        while (start < value.Length)
        {
            var remaining = value.Length - start;
            var take = Math.Min(remaining, maxChars);

            // Don't split in the middle of a surrogate pair
            if (take < remaining && char.IsHighSurrogate(value[start + take - 1]))
                take--;

            chunks.Add(value.Substring(start, take));
            start += take;
        }

        return chunks;
    }

    /// <summary>
    /// Estimate total character count across all chunks plus overhead.
    /// </summary>
    private static int EstimateTotalChars(List<string> chunks)
    {
        var total = 200; // overhead for PartitionKey, RowKey, metadata properties
        for (int i = 0; i < chunks.Count; i++)
            total += chunks[i].Length + 20; // chunk + property name
        return total;
    }

    /// <summary>
    /// Escape single quotes in OData filter values to prevent injection.
    /// </summary>
    private static string EscapeFilter(string value) => value.Replace("'", "''");
}
