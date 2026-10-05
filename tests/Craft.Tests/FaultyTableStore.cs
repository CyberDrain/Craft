using Craft.Storage;

namespace Craft.Tests;

/// <summary>Fails chosen writes, so a test can stop a sequence of writes at an exact point.</summary>
internal sealed class FaultyTableStore(ICraftTableStore inner) : ICraftTableStore
{
    public Func<string, string, string, bool>? FailUpsert;   // (table, pk, rk) => fail
    public Func<string, IReadOnlyList<StoreOp>, bool>? FailSubmit;
    public Func<string, string, bool>? FailDelete;           // (table, rk) => fail

    public Task PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
    public Task EnsureTableAsync(string table, CancellationToken ct = default) => inner.EnsureTableAsync(table, ct);
    public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default) =>
        FailUpsert?.Invoke(table, row.PartitionKey, row.RowKey) == true
            ? throw new InvalidOperationException($"injected upsert failure on {table}")
            : inner.UpsertAsync(table, row, ct);
    public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default) =>
        inner.UpsertBatchAsync(table, partitionKey, rows, ct);
    public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default) =>
        inner.TryReplaceBatchAsync(table, partitionKey, rows, ct);
    public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default) =>
        inner.GetAsync(table, partitionKey, rowKey, ct);
    public IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey, CancellationToken ct = default) =>
        inner.QueryPartitionAsync(table, partitionKey, ct);
    public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, CancellationToken ct = default) => inner.QueryTableAsync(table, ct);
    public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, int maxPerPage, CancellationToken ct = default) =>
        inner.QueryTableAsync(table, filter, maxPerPage, ct);
    public IAsyncEnumerable<StoreRow> QueryRowKeyRangeAsync(string table, string partitionKey, string fromRowKey, string toRowKey,
        IReadOnlyList<string>? properties = null, int? maxPerPage = null, CancellationToken ct = default) =>
        inner.QueryRowKeyRangeAsync(table, partitionKey, fromRowKey, toRowKey, properties, maxPerPage, ct);
    public Task<bool> TrySubmitAsync(string table, string partitionKey, IReadOnlyList<StoreOp> ops, CancellationToken ct = default) =>
        FailSubmit?.Invoke(table, ops) == true
            ? throw new InvalidOperationException($"injected transaction failure on {table}")
            : inner.TrySubmitAsync(table, partitionKey, ops, ct);
    public Task DeleteTableAsync(string table, CancellationToken ct = default) => inner.DeleteTableAsync(table, ct);
    public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default) =>
        FailDelete?.Invoke(table, rowKey) == true
            ? throw new InvalidOperationException($"injected delete failure on {table}")
            : inner.DeleteAsync(table, partitionKey, rowKey, ct);
    public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default) =>
        inner.DeletePartitionAsync(table, partitionKey, ct);
}
