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
