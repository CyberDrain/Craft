using System.Collections.Concurrent;
using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// In-memory <see cref="ICraftTableStore"/> for unit tests. Deliberately IGNORES the optional
/// <c>filter</c> on queries — exactly what the interface permits a backend to do — so tests also verify
/// that callers re-apply their own predicate to the full result. Not thread-safe beyond the concurrent
/// dictionaries; tests drive it single-threaded.
/// </summary>
internal sealed class FakeTableStore : ICraftTableStore
{
    // table -> (partitionKey|rowKey) -> row
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StoreRow>> _tables = new();

    private static string Key(string pk, string rk) => pk + "" + rk;
    private ConcurrentDictionary<string, StoreRow> Table(string table) =>
        _tables.GetOrAdd(table, _ => new ConcurrentDictionary<string, StoreRow>());

    /// <summary>All rows currently in a table (test helper).</summary>
    public IReadOnlyList<StoreRow> All(string table) => Table(table).Values.ToList();

    public int Count(string table) => Table(table).Count;

    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task EnsureTableAsync(string table, CancellationToken ct = default) { Table(table); return Task.CompletedTask; }

    public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
    {
        Table(table)[Key(row.PartitionKey, row.RowKey)] = row;
        return Task.CompletedTask;
    }

    public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
    {
        foreach (var r in rows) Table(table)[Key(r.PartitionKey, r.RowKey)] = r;
        return Task.CompletedTask;
    }

    public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
    {
        foreach (var r in rows) Table(table)[Key(r.PartitionKey, r.RowKey)] = r;
        return Task.FromResult(true);
    }

    public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default) =>
        Task.FromResult(Table(table).TryGetValue(Key(partitionKey, rowKey), out var r) ? r : null);

    public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in Table(table).Values.Where(r => r.PartitionKey == partitionKey)) { yield return r; }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Snapshot so deletes during enumeration (the purge path) don't throw.
        foreach (var r in Table(table).Values.ToList()) { yield return r; }
        await Task.CompletedTask;
    }

    public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        Table(table).TryRemove(Key(partitionKey, rowKey), out _);
        return Task.CompletedTask;
    }

    public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
    {
        foreach (var k in Table(table).Where(kv => kv.Value.PartitionKey == partitionKey).Select(kv => kv.Key).ToList())
            Table(table).TryRemove(k, out _);
        return Task.CompletedTask;
    }
}
