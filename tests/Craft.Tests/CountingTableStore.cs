using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// Counts what reaches the table backend, per table: point reads, queries, the rows they returned and the
/// pages that would have taken, transactions and writes. Wraps another store and forwards everything.
/// Pages are rows actually consumed divided by the page size asked for (1,000 when none), so a query the
/// caller stops reading early costs only the pages it read, as with the Azure SDK's lazy paging.
/// </summary>
internal sealed class CountingTableStore(ICraftTableStore inner) : ICraftTableStore
{
    public sealed class Counts
    {
        public int PointReads, Queries, Rows, Pages, Submits, Upserts, BatchUpserts, Deletes;
        public override string ToString() =>
            $"reads={PointReads} queries={Queries} rows={Rows} pages={Pages} submits={Submits} upserts={Upserts} batches={BatchUpserts} deletes={Deletes}";
    }

    private readonly ConcurrentDictionary<string, Counts> _byTable = new(StringComparer.Ordinal);

    public Counts For(string table) => _byTable.GetOrAdd(table, _ => new Counts());
    public void Reset() => _byTable.Clear();

    /// <summary>Every table's counts summed.</summary>
    public Counts Total()
    {
        var t = new Counts();
        foreach (var c in _byTable.Values)
        {
            t.PointReads += c.PointReads; t.Queries += c.Queries; t.Rows += c.Rows; t.Pages += c.Pages;
            t.Submits += c.Submits; t.Upserts += c.Upserts; t.BatchUpserts += c.BatchUpserts; t.Deletes += c.Deletes;
        }
        return t;
    }

    private async IAsyncEnumerable<StoreRow> Count(string table, IAsyncEnumerable<StoreRow> rows, int pageSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = For(table);
        Interlocked.Increment(ref c.Queries);
        Interlocked.Increment(ref c.Pages);
        var n = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            if (n > 0 && n % pageSize == 0) Interlocked.Increment(ref c.Pages);
            n++;
            Interlocked.Increment(ref c.Rows);
            yield return row;
        }
    }

    public Task PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
    public Task EnsureTableAsync(string table, CancellationToken ct = default) => inner.EnsureTableAsync(table, ct);

    public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).Upserts);
        return inner.UpsertAsync(table, row, ct);
    }

    public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).BatchUpserts);
        return inner.UpsertBatchAsync(table, partitionKey, rows, ct);
    }

    public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).Submits);
        return inner.TryReplaceBatchAsync(table, partitionKey, rows, ct);
    }

    public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).PointReads);
        return inner.GetAsync(table, partitionKey, rowKey, ct);
    }

    public IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey, CancellationToken ct = default) =>
        Count(table, inner.QueryPartitionAsync(table, partitionKey, ct), 1000, ct);

    public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, CancellationToken ct = default) =>
        Count(table, inner.QueryTableAsync(table, ct), 1000, ct);

    public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, CancellationToken ct = default) =>
        Count(table, inner.QueryTableAsync(table, filter, ct), 1000, ct);

    public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, IReadOnlyList<string>? properties,
        CancellationToken ct = default) =>
        Count(table, inner.QueryTableAsync(table, filter, properties, ct), 1000, ct);

    public IAsyncEnumerable<StoreRow> QueryTableAsync(string table, string? filter, int maxPerPage, CancellationToken ct = default) =>
        Count(table, inner.QueryTableAsync(table, filter, maxPerPage, ct), Math.Max(1, maxPerPage), ct);

    public IAsyncEnumerable<StoreRow> QueryRowKeyRangeAsync(string table, string partitionKey, string fromRowKey, string toRowKey,
        IReadOnlyList<string>? properties = null, CancellationToken ct = default) =>
        Count(table, inner.QueryRowKeyRangeAsync(table, partitionKey, fromRowKey, toRowKey, properties, ct), 1000, ct);

    public Task<bool> TrySubmitAsync(string table, string partitionKey, IReadOnlyList<StoreOp> ops, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).Submits);
        return inner.TrySubmitAsync(table, partitionKey, ops, ct);
    }

    public Task DeleteTableAsync(string table, CancellationToken ct = default) => inner.DeleteTableAsync(table, ct);

    public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).Deletes);
        return inner.DeleteAsync(table, partitionKey, rowKey, ct);
    }

    public Task DeleteBatchAsync(string table, string partitionKey, IReadOnlyList<string> rowKeys, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).Deletes);
        return inner.DeleteBatchAsync(table, partitionKey, rowKeys, ct);
    }

    public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
    {
        Interlocked.Increment(ref For(table).Deletes);
        return inner.DeletePartitionAsync(table, partitionKey, ct);
    }
}
