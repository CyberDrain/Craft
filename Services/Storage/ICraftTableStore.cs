namespace Craft.Storage;

/// <summary>
/// Persistence for the host's own state — the allowedUsers RBAC table and the orchestrator's
/// run/task/result tables. Rows are addressed by (partition key, row key) and carry a property bag,
/// following Azure Table Storage semantics. Implemented by <see cref="AzureTableStore"/>.
///
/// This is host state only — the hosted PowerShell app (CIPP-NG) accesses its own tables directly and
/// is out of scope here.
/// </summary>
public interface ICraftTableStore
{
    /// <summary>
    /// Lightweight reachability probe for the backend (e.g. list-tables / SELECT 1). Completes on
    /// success; throws if the backend is unreachable or authentication fails. Used by the health
    /// endpoint to report storage readiness without doing real work.
    /// </summary>
    Task PingAsync(CancellationToken ct = default);

    /// <summary>Create the table/collection if it does not already exist.</summary>
    Task EnsureTableAsync(string table, CancellationToken ct = default);

    /// <summary>Insert or replace a single row (by its partition + row key).</summary>
    Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default);

    /// <summary>
    /// Insert or replace many rows that share one partition key. Implementations should apply this as
    /// atomically as the backend allows and internally chunk to any per-request limits.
    /// </summary>
    Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default);

    /// <summary>
    /// Replace rows that share a partition key, each guarded by the <see cref="StoreRow.ETag"/> it was
    /// read with. All-or-nothing and never partially applied: if any row has changed since it was read,
    /// nothing is written and this returns false.
    ///
    /// This is the claim primitive. Unlike <see cref="UpsertBatchAsync"/> it must NOT fall back to
    /// unconditional writes when the transaction fails — a rejected conditional write means something
    /// else owns those rows now, and forcing it through would take work another worker is already doing.
    ///
    /// Implementations may cap the batch at the backend's transaction limit; callers are expected to
    /// stay well under it (a claim is worker-pool sized, not run sized).
    /// </summary>
    /// <returns>True if every row was replaced; false if the guard failed and nothing was written.</returns>
    Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
        CancellationToken ct = default);

    /// <summary>Fetch a single row, or null if it does not exist.</summary>
    Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default);

    /// <summary>Stream every row in a partition.</summary>
    IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey, CancellationToken ct = default);

    /// <summary>Stream every row in the table (all partitions).</summary>
    IAsyncEnumerable<StoreRow> QueryTableAsync(string table, CancellationToken ct = default);

    /// <summary>
    /// Stream rows matching a backend-native filter expression — an OData <c>$filter</c> for Azure
    /// Tables — so the narrowing happens server-side instead of over the wire.
    ///
    /// Purely an optimisation, and deliberately so: this default implementation IGNORES the filter and
    /// returns everything, which keeps a backend that cannot push predicates down correct without
    /// implementing anything. Callers must therefore re-apply the same predicate to whatever comes
    /// back, and must never depend on the filter having been honoured.
    ///
    /// It exists for the job queue's claim path, which runs once per pump tick against the whole queue
    /// table. Unfiltered, a large backlog is paged to the client on every poll just to find the handful
    /// of rows that are actually free.
    /// </summary>
    IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, CancellationToken ct = default)
        => QueryTableAsync(table, ct);

    /// <summary>
    /// The filtered scan, additionally projected to <paramref name="properties"/> (a backend that
    /// honours it still returns the keys and Timestamp) so that wide rows are not shipped just to read
    /// their keys.
    ///
    /// Same contract as the filter: an optimisation a backend may ignore. This default returns full
    /// rows, so a caller must only ever READ the properties it asked for and must not take a property's
    /// absence to mean anything.
    /// </summary>
    IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, IReadOnlyList<string>? properties,
        CancellationToken ct = default)
        => QueryTableAsync(table, filter, ct);

    /// <summary>The filtered scan, fetched <paramref name="maxPerPage"/> rows per request, for callers that
    /// stop after the first few matches. Same contract as the filter: a backend may ignore both.</summary>
    IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, int maxPerPage,
        CancellationToken ct = default)
        => QueryTableAsync(table, filter, ct);

    /// <summary>
    /// Rows of one partition with <paramref name="fromRowKey"/> &lt;= RowKey &lt; <paramref name="toRowKey"/>
    /// (ordinal), optionally projected (name the keys too if you read them). Split entities are not
    /// reassembled, so use it only on tables whose rows are never split. <paramref name="maxPerPage"/> is
    /// the page size asked of the service (<c>$top</c>); a caller that needs a few rows should pass it, or each
    /// request returns up to 1,000.
    /// </summary>
    async IAsyncEnumerable<StoreRow> QueryRowKeyRangeAsync(string table, string partitionKey, string fromRowKey,
        string toRowKey, IReadOnlyList<string>? properties = null, int? maxPerPage = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in QueryPartitionAsync(table, partitionKey, ct))
            if (string.CompareOrdinal(row.RowKey, fromRowKey) >= 0 && string.CompareOrdinal(row.RowKey, toRowKey) < 0)
                yield return row;
    }

    /// <summary>
    /// Apply <paramref name="ops"/> to one partition as a single all-or-nothing transaction (at most 100
    /// ops, rows never split). Insert fails if the row exists; Replace and a Delete carrying an ETag fail if
    /// the row changed or is gone. Returns false, with nothing written, when any guard fails.
    ///
    /// This default checks every guard and then applies the ops one by one, which is atomic only for a
    /// single-threaded caller; <see cref="AzureTableStore"/> submits a real transaction.
    /// </summary>
    async Task<bool> TrySubmitAsync(string table, string partitionKey, IReadOnlyList<StoreOp> ops,
        CancellationToken ct = default)
    {
        foreach (var op in ops)
        {
            var current = await GetAsync(table, op.Row.PartitionKey, op.Row.RowKey, ct);
            var ok = op.Kind switch
            {
                StoreOpKind.Insert => current == null,
                StoreOpKind.Replace => current != null && current.ETag == op.Row.ETag,
                StoreOpKind.Delete => op.Row.ETag == null || (current != null && current.ETag == op.Row.ETag),
                _ => true,
            };
            if (!ok) return false;
        }
        foreach (var op in ops)
        {
            if (op.Kind == StoreOpKind.Delete) await DeleteAsync(table, op.Row.PartitionKey, op.Row.RowKey, ct);
            else await UpsertAsync(table, op.Row, ct);
        }
        return true;
    }

    /// <summary>Delete a whole table if it exists. The default does nothing.</summary>
    Task DeleteTableAsync(string table, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Delete a single row. A missing row is not an error.</summary>
    Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default);

    /// <summary>
    /// Delete many rows that share a partition key, in as few round-trips as the backend allows. A
    /// missing row is never an error.
    ///
    /// This default keeps a backend that cannot batch correct by deleting one row at a time;
    /// <see cref="AzureTableStore"/> overrides it with a per-partition transaction. Callers may pass
    /// more than a single transaction can hold — implementations chunk internally.
    /// </summary>
    async Task DeleteBatchAsync(string table, string partitionKey, IReadOnlyList<string> rowKeys,
        CancellationToken ct = default)
    {
        foreach (var rowKey in rowKeys)
            await DeleteAsync(table, partitionKey, rowKey, ct);
    }

    /// <summary>Delete every row in a partition.</summary>
    Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default);
}

public enum StoreOpKind { Insert, Replace, Delete, Upsert }

/// <summary>One write in a <see cref="ICraftTableStore.TrySubmitAsync"/> transaction. Replace and Delete are
/// guarded by <see cref="StoreRow.ETag"/> (a Delete without one is unconditional).</summary>
public readonly record struct StoreOp(StoreOpKind Kind, StoreRow Row)
{
    public static StoreOp Insert(StoreRow row) => new(StoreOpKind.Insert, row);
    public static StoreOp Replace(StoreRow row) => new(StoreOpKind.Replace, row);
    public static StoreOp Upsert(StoreRow row) => new(StoreOpKind.Upsert, row);
    public static StoreOp Delete(StoreRow row) => new(StoreOpKind.Delete, row);
}
