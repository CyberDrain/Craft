using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;

namespace Craft.Services;

/// <summary>
/// Azure Table Storage implementation of <see cref="ICraftTableStore"/>. This is the only file that
/// references <c>Azure.Data.Tables</c>. All Azure-specific concerns — 100-entity transaction batches,
/// the ~4 MB transaction cap, OData filter escaping, and 404 handling — are contained here.
/// </summary>
public sealed class AzureTableStore : ICraftTableStore
{
    private readonly Lazy<string> _connectionString;
    private readonly ConcurrentDictionary<string, TableClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private TableServiceClient? _service;

    // Azure Table transaction limits: at most 100 entities, all sharing a partition key, ~4 MB total.
    private const int MaxBatch = 100;
    private const int MaxBatchChars = 1_600_000; // ≈3.2 MB UTF-16, safely under the 4 MB cap

    private static readonly HashSet<string> s_systemKeys = new(StringComparer.Ordinal)
    {
        "PartitionKey", "RowKey", "Timestamp", "odata.etag"
    };

    public AzureTableStore(CraftSettings settings)
    {
        // Resolved lazily so constructing the store on a role that never touches storage does not
        // require a connection string — it is only resolved on first actual use. Prefers the explicit
        // RBAC-table override for backward compatibility; else the shared AzureWebJobsStorage connection.
        _connectionString = new Lazy<string>(() =>
            settings.Storage.ResolveConnection(settings.Auth.UserStorageConnection, "table storage"));
    }

    private TableClient Client(string table) =>
        _clients.GetOrAdd(table, t => new TableClient(_connectionString.Value, t));

    private TableServiceClient Service => _service ??= new TableServiceClient(_connectionString.Value);

    public async Task PingAsync(CancellationToken ct = default)
    {
        // List tables (one page) — confirms connectivity + auth without touching app data.
        await foreach (var _ in Service.QueryAsync(maxPerPage: 1, cancellationToken: ct))
            break;
    }

    public Task EnsureTableAsync(string table, CancellationToken ct = default) =>
        Client(table).CreateIfNotExistsAsync(ct);

    public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default) =>
        Client(table).UpsertEntityAsync(ToEntity(row), TableUpdateMode.Replace, ct);

    public async Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
    {
        var client = Client(table);
        var batch = new List<TableTransactionAction>(MaxBatch);
        var chars = 0;

        foreach (var row in rows)
        {
            var rowChars = EstimateChars(row);
            if (batch.Count > 0 && (batch.Count >= MaxBatch || chars + rowChars > MaxBatchChars))
            {
                await SubmitAsync(client, batch, ct);
                batch.Clear();
                chars = 0;
            }
            batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, ToEntity(row)));
            chars += rowChars;
        }

        if (batch.Count > 0)
            await SubmitAsync(client, batch, ct);
    }

    private static async Task SubmitAsync(TableClient client, List<TableTransactionAction> batch, CancellationToken ct)
    {
        try
        {
            await client.SubmitTransactionAsync(batch, ct);
        }
        catch (Exception)
        {
            // A transaction is all-or-nothing; on failure fall back to individual upserts so one bad
            // entity (or a transient 4xx) doesn't drop the whole batch.
            foreach (var action in batch)
            {
                try { await client.UpsertEntityAsync((TableEntity)action.Entity, TableUpdateMode.Replace, ct); }
                catch { /* best-effort fallback */ }
            }
        }
    }

    public async Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        try
        {
            var response = await Client(table).GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);
            return ToRow(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = $"PartitionKey eq '{Escape(partitionKey)}'";
        await foreach (var entity in Client(table).QueryAsync<TableEntity>(filter: filter, cancellationToken: ct))
            yield return ToRow(entity);
    }

    public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entity in Client(table).QueryAsync<TableEntity>(cancellationToken: ct))
            yield return ToRow(entity);
    }

    public async Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        try
        {
            await Client(table).DeleteEntityAsync(partitionKey, rowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone — not an error.
        }
    }

    public async Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
    {
        var client = Client(table);
        var keys = new List<(string pk, string rk)>();
        await foreach (var entity in client.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{Escape(partitionKey)}'",
            select: new[] { "PartitionKey", "RowKey" },
            cancellationToken: ct))
        {
            keys.Add((entity.PartitionKey, entity.RowKey));
        }

        for (int i = 0; i < keys.Count; i += MaxBatch)
        {
            var batch = keys.Skip(i).Take(MaxBatch)
                .Select(k => new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(k.pk, k.rk)))
                .ToList();
            try
            {
                await client.SubmitTransactionAsync(batch, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted — safe to ignore.
            }
        }
    }

    // ── Conversion ────────────────────────────────────────────────────────────

    private static TableEntity ToEntity(StoreRow row)
    {
        var entity = new TableEntity(row.PartitionKey, row.RowKey);
        foreach (var (key, value) in row.Properties)
            entity[key] = value;
        return entity;
    }

    private static StoreRow ToRow(TableEntity entity)
    {
        var row = new StoreRow(entity.PartitionKey, entity.RowKey) { Timestamp = entity.Timestamp };
        foreach (var (key, value) in entity)
        {
            if (s_systemKeys.Contains(key)) continue;
            row.Properties[key] = value;
        }
        return row;
    }

    private static int EstimateChars(StoreRow row)
    {
        var total = row.PartitionKey.Length + row.RowKey.Length + 128;
        foreach (var value in row.Properties.Values)
            if (value is string s) total += s.Length + 20;
            else total += 24;
        return total;
    }

    /// <summary>Escape single quotes in OData filter values to prevent injection.</summary>
    private static string Escape(string value) => value.Replace("'", "''");
}
