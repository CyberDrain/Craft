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
    // The payload budget lives in EntitySplitter.MaxTransactionPayload, used by SubmitSizedAsync.
    private const int MaxBatch = 100;

    private static readonly HashSet<string> s_systemKeys = new(StringComparer.Ordinal)
    {
        "PartitionKey", "RowKey", "Timestamp", "odata.etag"
    };

    private readonly ILogger<AzureTableStore>? _logger;

    public AzureTableStore(CraftSettings settings, ILogger<AzureTableStore>? logger = null)
    {
        // Resolved lazily so constructing the store on a role that never touches storage does not
        // require a connection string — it is only resolved on first actual use. Prefers the explicit
        // RBAC-table override for backward compatibility; else the shared AzureWebJobsStorage connection.
        _connectionString = new Lazy<string>(() =>
            settings.Storage.ResolveConnection(settings.Auth.UserStorageConnection, "table storage"));
        _clientOptions = BuildClientOptions(settings.Storage);
        _logger = logger;
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

    /// <summary>
    /// How long an operation that found its table missing waits for the service to allow the table to
    /// be created again. A deleted table is "being deleted" for a while — Azure documents "at least 40
    /// seconds"; measured at 62 s on a real account — and create answers 409 TableBeingDeleted until
    /// then. (During that window the service still accepts reads and writes to the doomed table and
    /// then discards them with it; nothing a client can detect, so the 404 that follows is the first
    /// real signal.) Azurite has no such window. The wait honours the caller's cancellation token, so
    /// a status-writer flush that times out simply requeues the write and the next flush resumes.
    /// </summary>
    private static readonly TimeSpan TableRecreateWait = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan TableRecreatePoll = TimeSpan.FromSeconds(3);

    public Task EnsureTableAsync(string table, CancellationToken ct = default) => CreateTableAsync(table, ct);

    /// <summary>
    /// The service's answer for a table that does not exist: 404 with error code TableNotFound. A row
    /// that does not exist is also a 404, but ResourceNotFound, so the code is what separates "row is
    /// gone" (routine) from "the whole table is gone" (deleted out from under a live host). Transactions
    /// report it the same way, through <see cref="TableTransactionFailedException"/>.
    /// </summary>
    private static bool IsTableNotFound(RequestFailedException ex) =>
        ex.Status == 404 && (
            string.Equals(ex.ErrorCode, "TableNotFound", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("TableNotFound", StringComparison.OrdinalIgnoreCase));

    private async Task CreateTableAsync(string table, CancellationToken ct)
    {
        var client = Client(table);
        var deadline = DateTime.UtcNow + TableRecreateWait;
        while (true)
        {
            try
            {
                await client.CreateIfNotExistsAsync(ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 409
                && string.Equals(ex.ErrorCode, "TableBeingDeleted", StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TableRecreatePoll, ct);
            }
        }
    }

    /// <summary>
    /// An operation just learned its table no longer exists. Put the table back so the caller can run
    /// the operation again. A table deleted through table maintenance, or by a reset that cleared the
    /// orchestrator's state, must not take the host's writes down with it until the next restart — and
    /// it did, silently: the batch path's per-entity fallback failed the same way and swallowed it.
    /// If the table cannot be created (still being deleted past <see cref="TableRecreateWait"/>), this
    /// throws and so does the operation, which is the right outcome for a write that cannot land.
    /// </summary>
    private async Task RecreateTableAsync(string table, CancellationToken ct)
    {
        await CreateTableAsync(table, ct);
        _logger?.LogWarning("[TableStore] Table {Table} was missing and has been recreated; retrying the operation that noticed", table);
    }

    public async Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
    {
        var client = Client(table);
        var split = EntitySplitter.Split(ToEntity(row));

        // Fast path: the entity fits one row unchanged — a single unconditional upsert, exactly as
        // before large-entity splitting existed. A key that WAS split earlier and is now small leaves
        // its extra "{RowKey}-part{n}" rows behind, but Reassemble's plain-row precedence means the
        // read still returns this value, and the next engaged write of the key removes them.
        if (!split.Engaged)
        {
            var entity = split.Rows[0];
            try
            {
                await client.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
            }
            catch (RequestFailedException ex) when (IsTableNotFound(ex))
            {
                await RecreateTableAsync(table, ct);
                await client.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
            }
            return;
        }

        var actions = split.Rows
            .Select(r => new TableTransactionAction(TableTransactionActionType.UpsertReplace, r))
            .ToList();
        await SubmitSizedAsync(table, client, actions, ct);
        await RemoveStalePartRowsAsync(table, row.PartitionKey, row.RowKey,
            new HashSet<string>(split.Rows.Select(r => r.RowKey), StringComparer.Ordinal), ct);
    }

    public async Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
    {
        var client = Client(table);
        var actions = new List<TableTransactionAction>(rows.Count);
        var engaged = new List<(string PartitionKey, string RowKey, HashSet<string> Live)>();

        foreach (var row in rows)
        {
            var split = EntitySplitter.Split(ToEntity(row));
            foreach (var r in split.Rows)
                actions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, r));
            if (split.Engaged)
                engaged.Add((row.PartitionKey, row.RowKey,
                    new HashSet<string>(split.Rows.Select(r => r.RowKey), StringComparer.Ordinal)));
        }

        await SubmitSizedAsync(table, client, actions, ct);

        // Only an engaged (split) write can leave stale part rows behind, so the common all-small batch
        // does no extra reads at all.
        foreach (var (pk, rk, live) in engaged)
            await RemoveStalePartRowsAsync(table, pk, rk, live, ct);
    }

    public async Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return true;

        var actions = new List<TableTransactionAction>(rows.Count);
        var engaged = new List<(string PartitionKey, string RowKey, HashSet<string> Live)>();

        foreach (var row in rows)
        {
            // A row with no ETag was never read from storage, so there is nothing to guard against and
            // "replace whatever is there" is not a claim. Refuse rather than race.
            if (string.IsNullOrEmpty(row.ETag))
                throw new ArgumentException($"Row {row.PartitionKey}/{row.RowKey} has no ETag to guard the write.", nameof(rows));

            var split = EntitySplitter.Split(ToEntity(row));
            foreach (var r in split.Rows)
            {
                // The concurrency guard belongs on the entity's own row — splitting never changes that
                // RowKey. The extra "{RowKey}-part{n}" rows carry no independent token and ride along
                // unconditionally in the SAME atomic transaction, so the claim stays all-or-nothing.
                if (string.Equals(r.RowKey, row.RowKey, StringComparison.Ordinal))
                    actions.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, r, new ETag(row.ETag)));
                else
                    actions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, r));
            }
            if (split.Engaged)
                engaged.Add((row.PartitionKey, row.RowKey,
                    new HashSet<string>(split.Rows.Select(r => r.RowKey), StringComparer.Ordinal)));
        }

        // One transaction, so one round-trip and one atomic outcome. A caller claiming more than a
        // transaction can hold would silently get partial application, which for a claim means rows
        // marked as owned by a worker that never receives them. Splitting a large guarded row can turn
        // one logical claim into several physical rows, so the cap is checked after splitting.
        if (actions.Count > MaxBatch)
            throw new ArgumentException($"Conditional batch is limited to {MaxBatch} rows, got {actions.Count} after large-entity splitting.", nameof(rows));

        try
        {
            await Client(table).SubmitTransactionAsync(actions, ct);
        }
        catch (RequestFailedException ex) when (IsTableNotFound(ex))
        {
            // No table, so no rows, so nothing was claimed. Put the table back for the writes that
            // follow; this claim itself is simply lost — there is nothing left to retry it against.
            await RecreateTableAsync(table, ct);
            return false;
        }
        catch (RequestFailedException ex) when (ex.Status is 412 or 404 or 409)
        {
            // 412 precondition failed / 409 conflict — someone else changed or claimed a row.
            // 404 — a row was deleted underneath us. All three mean "not ours", not "retry harder".
            // Deliberately NO fallback to unconditional upserts: that is what would steal the row.
            return false;
        }

        // The claim landed. Remove any part rows left by an earlier, larger version of a claimed entity
        // so a later read cannot merge stale fragments. Best-effort and post-commit: the claim already
        // succeeded and stale parts only ever affect a subsequent read.
        foreach (var (pk, rk, live) in engaged)
            await RemoveStalePartRowsAsync(table, pk, rk, live, ct);

        return true;
    }

    private async Task SubmitAsync(string table, TableClient client, List<TableTransactionAction> batch, CancellationToken ct)
    {
        try
        {
            await client.SubmitTransactionAsync(batch, ct);
            return;
        }
        catch (RequestFailedException ex) when (IsTableNotFound(ex))
        {
            // The table is gone, not the batch. Put it back and submit the same transaction again; only
            // if THAT fails does the per-entity fallback below get its turn. Without this the fallback
            // ran against the missing table as well and swallowed every one of its failures, so a
            // deleted table lost every status write until the next restart without a line in the log.
            await RecreateTableAsync(table, ct);
            try
            {
                await client.SubmitTransactionAsync(batch, ct);
                return;
            }
            catch (Exception)
            {
                // Fall through to the per-entity fallback.
            }
        }
        catch (Exception)
        {
            // Fall through to the per-entity fallback.
        }

        // A transaction is all-or-nothing; on failure fall back to individual upserts so one bad
        // entity (or a transient 4xx) doesn't drop the whole batch.
        foreach (var action in batch)
        {
            var entity = (TableEntity)action.Entity;
            try { await client.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct); }
            catch (Exception exEntity)
            {
                // Best-effort fallback, but no longer silent: a genuinely poison row (an illegal key
                // remnant, an oversized property) fails here identically forever, and swallowing it with
                // no line in the log is how a task row can go missing while its seeded run counter still
                // expects it — visible only later as a run that will not finalize. The row is still
                // dropped (nothing else can be done in a fallback), but now it is at least reported.
                try
                {
                    _logger?.LogWarning(exEntity,
                        "[TableStore] Dropped a row that failed both the batch and its individual retry: {Table} {Pk}/{Rk}",
                        table, entity.PartitionKey, entity.RowKey);
                }
                catch { /* logging is never worth failing the fallback loop */ }
            }
        }
    }

    public async Task DeleteBatchAsync(string table, string partitionKey, IReadOnlyList<string> rowKeys,
        CancellationToken ct = default)
    {
        if (rowKeys.Count == 0) return;
        var client = Client(table);

        for (int i = 0; i < rowKeys.Count; i += MaxBatch)
        {
            var chunk = rowKeys.Skip(i).Take(MaxBatch).ToList();
            var batch = chunk
                .Select(rk => new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(partitionKey, rk)))
                .ToList();
            try
            {
                await client.SubmitTransactionAsync(batch, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // A transaction is all-or-nothing, so one already-deleted row (or a missing table) 404s
                // the whole chunk. Fall back to per-row deletes, which tolerate a missing row (and a
                // missing table) individually — the removal is idempotent either way.
                foreach (var rk in chunk)
                    await DeleteAsync(table, partitionKey, rk, ct);
            }
        }
    }

    public async Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        TableEntity entity;
        try
        {
            entity = (await Client(table).GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (IsTableNotFound(ex))
        {
            // The row cannot exist, which is the same answer as below — but bring the table back now
            // rather than leaving that to whichever write comes next.
            await RecreateTableAsync(table, ct);
            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        // No split markers → this row is the whole entity. The hot path, unchanged.
        if (!HasSplitMarkers(entity))
            return ToRow(entity);

        // A column-only chunked row reassembles from itself; a cross-row root needs its sibling
        // "{RowKey}-part{n}" rows fetched first. Try in place, and only re-query the partition when this
        // one row is not the whole entity.
        IncompleteEntityException? incomplete = null;
        var self = EntitySplitter.Reassemble(new[] { entity }, OnReassemblyWarning, ex => incomplete = ex).FirstOrDefault();
        if (incomplete is null && self is not null)
            return ToRow(self);

        var full = await ReadLogicalEntityAsync(table, partitionKey, rowKey, ct);
        return full is null ? null : ToRow(full);
    }

    /// <summary>
    /// Enumerates a query, recreating the table and starting over if the first page reports it missing.
    /// The retry enumerates the fresh table — empty, but that is the query's answer, and it proves the
    /// table is back. A table that vanishes mid-stream propagates; paging across a delete is not a case
    /// worth hiding.
    /// </summary>
    private async IAsyncEnumerable<TableEntity> EnumerateAsync(string table, Func<AsyncPageable<TableEntity>> query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var recreated = false;
        while (true)
        {
            await using var rows = query().GetAsyncEnumerator(ct);
            bool moved;
            try
            {
                moved = await rows.MoveNextAsync();
            }
            catch (RequestFailedException ex) when (!recreated && IsTableNotFound(ex))
            {
                recreated = true;
                await RecreateTableAsync(table, ct);
                continue;
            }

            while (moved)
            {
                yield return rows.Current;
                moved = await rows.MoveNextAsync();
            }
            yield break;
        }
    }

    public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = $"PartitionKey eq '{Escape(partitionKey)}'";
        await foreach (var entity in StreamReassembledAsync(table, () => Client(table).QueryAsync<TableEntity>(filter: filter, cancellationToken: ct), ct))
            yield return ToRow(entity);
    }

    public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entity in StreamReassembledAsync(table, () => Client(table).QueryAsync<TableEntity>(cancellationToken: ct), ct))
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
        await foreach (var entity in StreamReassembledAsync(table, () => Client(table).QueryAsync<TableEntity>(filter: filter, cancellationToken: ct), ct))
            yield return ToRow(entity);
    }

    /// <summary>
    /// The filtered scan with a <c>$select</c>, for callers that want keys and a stamp rather than the
    /// row. The retention sweep reads every Results row's partition this way, and a Results row is a
    /// 64 KiB chunk of payload it has no use for.
    ///
    /// Unlike the full-row scans, this projected path does NOT reassemble large entities: the projection
    /// strips the split markers reassembly needs, and its only caller wants the physical keys anyway (it
    /// groups by partition to find and delete whole orphaned partitions, part rows included). Reassembly
    /// here would drop every split entity as "incomplete" and hide the very keys the sweep must see.
    /// </summary>
    public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, IReadOnlyList<string>? properties,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entity in EnumerateAsync(table, () => Client(table).QueryAsync<TableEntity>(filter: filter, select: properties, cancellationToken: ct), ct))
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

        // If this entity had been split across rows, its "{rowKey}-part{n}" rows are addressed by
        // nothing else, so a plain delete would orphan them — and a later partition scan would then
        // resurrect the deleted entity by reassembling those leftover parts. Remove them too (an empty
        // "live" set marks every part row stale). This is a keys-only scan that finds nothing for the
        // common unsplit row.
        await RemoveStalePartRowsAsync(table, partitionKey, rowKey,
            new HashSet<string>(StringComparer.Ordinal), ct);
    }

    public async Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
    {
        var client = Client(table);
        var keys = new List<(string pk, string rk)>();
        var filter = $"PartitionKey eq '{Escape(partitionKey)}'";
        await foreach (var entity in EnumerateAsync(table, () => client.QueryAsync<TableEntity>(filter: filter, select: select, cancellationToken: ct), ct))
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

    // ── Large-entity splitting ─────────────────────────────────────────────────
    //
    // Azure Table Storage caps a string property at 64 KiB (32K UTF-16 units) and an entity at 1 MiB.
    // A host payload that exceeds either — a scheduled task whose Parameters embed a whole policy
    // template, say — cannot be stored as one entity: the write 400s with PropertyValueTooLarge and the
    // row is lost, so the task can never be dispatched. EntitySplitter (vendored from AzBobbyTables via
    // CIPP.TableClient) breaks such an entity across extra properties and, if still too big, extra rows,
    // and reassembles it on read. This is contained here for the same reason the rest of the Azure
    // specifics are: nothing above ICraftTableStore knows a row was ever split.
    //
    // The orchestrator Results table does its own hand-rolled chunking with the marker names
    // "OriginalEntityId"/"PartIndex"; EntitySplitter deliberately uses "_Craft"-prefixed marker names so
    // the two schemes never touch. A Results part row is therefore invisible to the reassembly below
    // (its own reader still sees the physical rows it expects), and an already-chunked Results property
    // is under the size limit so it is never re-split.

    private void OnReassemblyWarning(string message) =>
        _logger?.LogWarning("[TableStore] {Message}", message);

    private static bool HasSplitMarkers(TableEntity entity) =>
        entity.ContainsKey(EntitySplitter.SplitOverPropsKey) ||
        entity.ContainsKey(EntitySplitter.OriginalEntityIdKey) ||
        entity.ContainsKey(EntitySplitter.PartCountKey);

    /// <summary>
    /// Submit upsert actions grouped by partition and packed by BOTH count and estimated payload size.
    /// Split rows each approach the per-row budget, so 100 of them in one transaction would blow the
    /// batch's ~4 MB payload cap; the size bound is what a plain count cannot see. Delegates each packed
    /// batch to <see cref="SubmitAsync"/> so the table-recreate and per-entity fallback still apply.
    /// </summary>
    private async Task SubmitSizedAsync(string table, TableClient client, List<TableTransactionAction> actions,
        CancellationToken ct)
    {
        foreach (var group in actions.GroupBy(a => a.Entity.PartitionKey))
        {
            var batch = new List<TableTransactionAction>();
            long size = 0;
            foreach (var action in group)
            {
                var actionSize = action.Entity is TableEntity te ? EntitySplitter.EstimateEntitySize(te) : 0;
                if (batch.Count > 0 && (batch.Count >= MaxBatch || size + actionSize > EntitySplitter.MaxTransactionPayload))
                {
                    await SubmitAsync(table, client, batch, ct);
                    batch.Clear();
                    size = 0;
                }
                batch.Add(action);
                size += actionSize;
            }
            if (batch.Count > 0)
                await SubmitAsync(table, client, batch, ct);
        }
    }

    /// <summary>
    /// Stream a query's rows, reassembling large entities, without materialising the whole result set:
    /// plain rows (the overwhelming majority) are yielded as they page in, and only split-part rows —
    /// those carrying a cross-row or column-split marker — are buffered, their missing siblings fetched,
    /// and the group reassembled at the end. A plain row supersedes leftover parts of the same identity,
    /// so a reassembled group whose identity a plain row already emitted is dropped, matching
    /// EntitySplitter's plain-row precedence. This is what keeps the Results streaming reader from having
    /// to hold a 50–150 MB run in memory just because reassembly exists.
    /// </summary>
    private async IAsyncEnumerable<TableEntity> StreamReassembledAsync(string table,
        Func<AsyncPageable<TableEntity>> query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var plainKeys = new HashSet<(string, string)>();
        List<TableEntity>? parts = null;

        await foreach (var entity in EnumerateAsync(table, query, ct))
        {
            if (entity.ContainsKey(EntitySplitter.OriginalEntityIdKey) || entity.ContainsKey(EntitySplitter.SplitOverPropsKey))
            {
                (parts ??= new List<TableEntity>()).Add(entity);
            }
            else
            {
                plainKeys.Add((entity.PartitionKey, entity.RowKey));
                yield return entity;
            }
        }

        if (parts is null) yield break;

        var recovered = await RecoverMissingPartRowsAsync(table, parts, ct);
        foreach (var reassembled in EntitySplitter.Reassemble(recovered, OnReassemblyWarning, inc => OnReassemblyWarning(inc.Message)))
        {
            if (plainKeys.Contains((reassembled.PartitionKey, reassembled.RowKey))) continue;
            yield return reassembled;
        }
    }

    /// <summary>
    /// Fetch the rows of any entity in <paramref name="rows"/> that is only partly present (its query
    /// filter had no reason to match every "{RowKey}-part{n}" row it was split over) and return the set
    /// with them added. Part rows are matched by a RowKey range within the entity's partition — an index
    /// seek that needs no marker property to be selected.
    /// </summary>
    private async Task<List<TableEntity>> RecoverMissingPartRowsAsync(string table, List<TableEntity> rows,
        CancellationToken ct)
    {
        var incomplete = EntitySplitter.FindIncompleteGroups(rows);
        if (incomplete.Count == 0) return rows;

        var seen = new HashSet<(string, string)>(rows.Select(r => (r.PartitionKey, r.RowKey)));

        foreach (var partitionGroup in incomplete.GroupBy(g => g.PartitionKey, StringComparer.Ordinal))
        {
            var partitionClause = $"PartitionKey eq '{Escape(partitionGroup.Key)}'";
            foreach (var entityId in partitionGroup.Select(g => g.EntityId).Distinct(StringComparer.Ordinal))
            {
                var filter = $"{partitionClause} and {BuildRowKeyPrefixClause(entityId)}";
                await foreach (var row in Client(table).QueryAsync<TableEntity>(filter: filter, cancellationToken: ct))
                {
                    if (seen.Add((row.PartitionKey, row.RowKey)))
                        rows.Add(row);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Read one logical entity by (partition, row) key, reassembling it from its physical rows. Used by
    /// <see cref="GetAsync"/> when a point read lands on a split entity's root row. Returns null when the
    /// entity does not exist or cannot be reassembled from the rows present.
    /// </summary>
    private async Task<TableEntity?> ReadLogicalEntityAsync(string table, string partitionKey, string rowKey,
        CancellationToken ct)
    {
        // The prefix range is an index seek catching the root row and every "{rowKey}-part{n}" row, but
        // also unrelated rows sharing the prefix; filter to rows that belong to this entity before
        // reassembling.
        var filter = $"PartitionKey eq '{Escape(partitionKey)}' and {BuildRowKeyPrefixClause(rowKey)}";
        var rows = new List<TableEntity>();
        await foreach (var row in Client(table).QueryAsync<TableEntity>(filter: filter, cancellationToken: ct))
        {
            if (row.RowKey == rowKey ||
                (row.TryGetValue(EntitySplitter.OriginalEntityIdKey, out var id) && id?.ToString() == rowKey))
                rows.Add(row);
        }

        IncompleteEntityException? incomplete = null;
        var entity = EntitySplitter.Reassemble(rows, OnReassemblyWarning, ex => incomplete = ex)
            .FirstOrDefault(e => e.RowKey == rowKey);

        if (incomplete is not null)
        {
            OnReassemblyWarning(incomplete.Message);
            return null;
        }

        return entity;
    }

    /// <summary>
    /// Delete the part rows of a split entity that this write did NOT rewrite — leftovers from an
    /// earlier, larger version, which would otherwise merge stale fragments into a later read. Part rows
    /// are found by the cross-row marker; the root row (RowKey == originalRowKey) is in
    /// <paramref name="live"/> for a current split write, and an empty <paramref name="live"/> (from a
    /// delete) marks every part row stale.
    /// </summary>
    private async Task RemoveStalePartRowsAsync(string table, string partitionKey, string originalRowKey,
        HashSet<string> live, CancellationToken ct)
    {
        var filter = $"PartitionKey eq '{Escape(partitionKey)}' and {EntitySplitter.OriginalEntityIdKey} eq '{Escape(originalRowKey)}'";
        var stale = new List<string>();
        await foreach (var row in Client(table).QueryAsync<TableEntity>(filter: filter, select: select, cancellationToken: ct))
        {
            if (!live.Contains(row.RowKey))
                stale.Add(row.RowKey);
        }

        if (stale.Count > 0)
            await DeleteBatchAsync(table, partitionKey, stale, ct);
    }

    /// <summary>
    /// An OData clause matching every RowKey beginning with <paramref name="prefix"/>: the smallest
    /// string sorting above the prefix is the prefix with its last character incremented.
    /// </summary>
    private static string BuildRowKeyPrefixClause(string prefix)
    {
        var lower = $"RowKey ge '{Escape(prefix)}'";
        var bound = prefix.ToCharArray();
        for (var i = bound.Length - 1; i >= 0; i--)
        {
            if (bound[i] < char.MaxValue)
            {
                bound[i]++;
                var upper = new string(bound, 0, i + 1);
                return $"({lower} and RowKey lt '{Escape(upper)}')";
            }
        }

        // Every character is already the maximum, so nothing sorts above the prefix.
        return $"({lower})";
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

    /// <summary>Escape single quotes in OData filter values to prevent injection.</summary>
    private static string Escape(string value) => value.Replace("'", "''");
}
