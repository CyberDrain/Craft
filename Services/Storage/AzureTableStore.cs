using System.Collections.Concurrent;
using Azure;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Craft.Configuration;

namespace Craft.Storage;

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

    // Shared client options carrying ONE HttpClientTransport (a single SocketsHttpHandler) that every
    // TableClient/TableServiceClient reuses, so all storage traffic rides one bounded connection pool.
    // Same connection-reuse/limit pattern the downstream AzBobbyTables module uses; here the cap defaults
    // on (see StorageSettings.MaxConnectionsPerServer) because CRAFT knows it runs on connection-capped
    // Azure App Service. Built once — this store is a singleton.
    private readonly TableClientOptions _clientOptions;

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
        _clientOptions = BuildClientOptions(settings.Storage);
    }

    /// <summary>
    /// Builds the shared <see cref="TableClientOptions"/> that every client reuses. All TableClients and
    /// the TableServiceClient share the single <see cref="HttpClientTransport"/> it carries, so they pool
    /// connections across one <see cref="SocketsHttpHandler"/>. That handler is optionally capped at
    /// <c>MaxConnectionsPerServer</c> (Azure Table is HTTP/1.1 with no multiplexing → one TCP connection
    /// per concurrent request, so this ceilings the outbound socket count during a job fan-out) and its
    /// pooled connections are recycled on <c>PooledConnectionLifetime</c> for DNS/SNAT hygiene. Both are
    /// configurable via <see cref="StorageSettings"/> or env, so a downstream app can tune or disable them.
    /// </summary>
    private static TableClientOptions BuildClientOptions(StorageSettings storage)
    {
        var maxConns = ParseIntEnv("CRAFT_STORAGE_MAX_CONNECTIONS_PER_SERVER") ?? storage.MaxConnectionsPerServer;
        var lifetimeMin = ParseIntEnv("CRAFT_STORAGE_POOLED_CONNECTION_LIFETIME_MINUTES") ?? storage.PooledConnectionLifetimeMinutes;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = lifetimeMin > 0 ? TimeSpan.FromMinutes(lifetimeMin) : Timeout.InfiniteTimeSpan,
        };
        // <= 0 → leave the SDK/runtime default (int.MaxValue, i.e. unbounded).
        if (maxConns > 0)
            handler.MaxConnectionsPerServer = maxConns;

        return new TableClientOptions { Transport = new HttpClientTransport(handler) };
    }

    private static int? ParseIntEnv(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : null;

    private TableClient Client(string table) =>
        _clients.GetOrAdd(table, t => new TableClient(_connectionString.Value, t, _clientOptions));

    private TableServiceClient Service => _service ??= new TableServiceClient(_connectionString.Value, _clientOptions);

    private static readonly string[] select = new[] { "PartitionKey", "RowKey" };

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

    public async Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return true;

        // One transaction, so one round-trip and one atomic outcome. A caller claiming more than a
        // transaction can hold would silently get partial application, which for a claim means rows
        // marked as owned by a worker that never receives them.
        if (rows.Count > MaxBatch)
            throw new ArgumentException($"Conditional batch is limited to {MaxBatch} rows, got {rows.Count}.", nameof(rows));

        var actions = new List<TableTransactionAction>(rows.Count);
        foreach (var row in rows)
        {
            // A row with no ETag was never read from storage, so there is nothing to guard against and
            // "replace whatever is there" is not a claim. Refuse rather than race.
            if (string.IsNullOrEmpty(row.ETag))
                throw new ArgumentException($"Row {row.PartitionKey}/{row.RowKey} has no ETag to guard the write.", nameof(rows));

            actions.Add(new TableTransactionAction(
                TableTransactionActionType.UpdateReplace, ToEntity(row), new ETag(row.ETag)));
        }

        try
        {
            await Client(table).SubmitTransactionAsync(actions, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 412 or 404 or 409)
        {
            // 412 precondition failed / 409 conflict — someone else changed or claimed a row.
            // 404 — a row was deleted underneath us. All three mean "not ours", not "retry harder".
            // Deliberately NO fallback to unconditional upserts: that is what would steal the row.
            return false;
        }
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

    /// <summary>
    /// The same scan, narrowed by an OData <c>$filter</c> the service evaluates. Rows that cannot match
    /// are never put on the wire, which is the difference between paging a whole backlog to the client
    /// on every pump tick and fetching only the rows that are actually claimable.
    /// </summary>
    public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entity in Client(table).QueryAsync<TableEntity>(filter: filter, cancellationToken: ct))
            yield return ToRow(entity);
    }

    /// <summary>
    /// The filtered scan with a <c>$select</c>, for callers that want keys and a stamp rather than the
    /// row. The retention sweep reads every Results row's partition this way, and a Results row is a
    /// 64 KiB chunk of payload it has no use for.
    /// </summary>
    public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, IReadOnlyList<string>? properties,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entity in Client(table).QueryAsync<TableEntity>(filter: filter, select: properties, cancellationToken: ct))
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
            select: select,
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
        // ETag is surfaced as a field rather than a property, so it survives the round-trip a conditional
        // write needs without polluting the caller's property bag (s_systemKeys drops it below).
        var row = new StoreRow(entity.PartitionKey, entity.RowKey)
        {
            Timestamp = entity.Timestamp,
            ETag = entity.ETag.ToString(),
        };
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
