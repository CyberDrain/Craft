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

    /// <summary>Fetch a single row, or null if it does not exist.</summary>
    Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default);

    /// <summary>Stream every row in a partition.</summary>
    IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey, CancellationToken ct = default);

    /// <summary>Stream every row in the table (all partitions).</summary>
    IAsyncEnumerable<StoreRow> QueryTableAsync(string table, CancellationToken ct = default);

    /// <summary>Delete a single row. A missing row is not an error.</summary>
    Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default);

    /// <summary>Delete every row in a partition.</summary>
    Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default);
}
