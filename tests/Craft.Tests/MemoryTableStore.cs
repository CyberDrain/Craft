using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// In-memory <see cref="ICraftTableStore"/> with the backend properties the orchestration relies on: rows come
/// back ordered by PartitionKey then RowKey, every write stamps a new ETag, and
/// <see cref="TrySubmitAsync"/> is all-or-nothing under one lock. Reads hand out copies, so mutating a row
/// read from here never writes it. Partition and row-key range reads are index seeks, as on Azure, so a test
/// with a large table pays for the rows it reads rather than for the whole table.
/// </summary>
internal sealed class MemoryTableStore : ICraftTableStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Table> _tables = new();
    private long _etag;

    /// <summary>Awaited before a transaction is checked — lets a test interleave a competing write.</summary>
    public Func<Task>? BeforeSubmit { get; set; }

    public int Submits { get; private set; }

    /// <summary>Tables deleted through <see cref="DeleteTableAsync"/>, in order.</summary>
    public List<string> DroppedTables { get; } = [];

    public Task DeleteTableAsync(string table, CancellationToken ct = default)
    {
        lock (_lock)
        {
            DroppedTables.Add(table);
            _tables.Remove(table);
        }
        return Task.CompletedTask;
    }

    private static readonly Comparer<(string, string)> Order = Comparer<(string, string)>.Create((a, b) =>
    {
        var c = string.CompareOrdinal(a.Item1, b.Item1);
        return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
    });

    private sealed class Table
    {
        public readonly Dictionary<(string, string), StoreRow> Rows = new();
        public readonly SortedSet<(string, string)> Keys = new(Order);

        public void Set((string, string) key, StoreRow row)
        {
            Rows[key] = row;
            Keys.Add(key);
        }

        public void Remove((string, string) key)
        {
            Rows.Remove(key);
            Keys.Remove(key);
        }

        /// <summary>Keys from <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive), in order.</summary>
        public IEnumerable<(string, string)> Between((string, string) from, (string, string) to) =>
            Order.Compare(from, to) >= 0 ? [] : Keys.GetViewBetween(from, to).Where(k => Order.Compare(k, to) < 0);
    }

    private Table Of(string t)
    {
        if (!_tables.TryGetValue(t, out var rows)) _tables[t] = rows = new Table();
        return rows;
    }

    private StoreRow Stamp(StoreRow r) => new(r.PartitionKey, r.RowKey)
    {
        ETag = $"W/\"{++_etag}\"",
        Timestamp = DateTimeOffset.UtcNow,
        Properties = new Dictionary<string, object?>(r.Properties),
    };

    private static StoreRow Copy(StoreRow r) => new(r.PartitionKey, r.RowKey)
    {
        ETag = r.ETag,
        Timestamp = r.Timestamp,
        Properties = new Dictionary<string, object?>(r.Properties),
    };

    public IReadOnlyList<StoreRow> All(string table)
    {
        lock (_lock)
        {
            var t = Of(table);
            return t.Keys.Select(k => Copy(t.Rows[k])).ToList();
        }
    }

    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task EnsureTableAsync(string table, CancellationToken ct = default)
    {
        lock (_lock) Of(table);
        return Task.CompletedTask;
    }

    public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
    {
        lock (_lock) Of(table).Set((row.PartitionKey, row.RowKey), Stamp(row));
        return Task.CompletedTask;
    }

    public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
    {
        lock (_lock) foreach (var r in rows) Of(table).Set((r.PartitionKey, r.RowKey), Stamp(r));
        return Task.CompletedTask;
    }

    public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default) =>
        TrySubmitAsync(table, partitionKey, rows.Select(StoreOp.Replace).ToList(), ct);

    public async Task<bool> TrySubmitAsync(string table, string partitionKey, IReadOnlyList<StoreOp> ops, CancellationToken ct = default)
    {
        if (BeforeSubmit is { } before) await before();
        return Submit(table, ops);
    }

    private bool Submit(string table, IReadOnlyList<StoreOp> ops)
    {
        lock (_lock)
        {
            var t = Of(table);
            foreach (var op in ops)
            {
                t.Rows.TryGetValue((op.Row.PartitionKey, op.Row.RowKey), out var cur);
                var ok = op.Kind switch
                {
                    StoreOpKind.Insert => cur == null,
                    StoreOpKind.Replace => cur != null && cur.ETag == op.Row.ETag,
                    StoreOpKind.Delete => op.Row.ETag == null || (cur != null && cur.ETag == op.Row.ETag),
                    _ => true,
                };
                if (!ok) return false;
            }
            foreach (var op in ops)
            {
                var key = (op.Row.PartitionKey, op.Row.RowKey);
                if (op.Kind == StoreOpKind.Delete) t.Remove(key);
                else t.Set(key, Stamp(op.Row));
            }
            Submits++;
            return true;
        }
    }

    public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(Of(table).Rows.TryGetValue((partitionKey, rowKey), out var r) ? Copy(r) : null);
    }

    private List<StoreRow> Snapshot(string table, Func<Table, IEnumerable<(string, string)>> keys)
    {
        lock (_lock)
        {
            var t = Of(table);
            return keys(t).Select(k => Copy(t.Rows[k])).ToList();
        }
    }

    public async IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in Snapshot(table, t => t.Between((partitionKey, ""), (partitionKey + "\0", ""))))
        {
            yield return r;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<StoreRow> QueryRowKeyRangeAsync(string table, string partitionKey, string fromRowKey,
        string toRowKey, IReadOnlyList<string>? properties = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in Snapshot(table, t => t.Between((partitionKey, fromRowKey), (partitionKey, toRowKey))))
        {
            yield return r;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<StoreRow> QueryTableAsync(string table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in Snapshot(table, t => t.Keys))
        {
            yield return r;
            await Task.Yield();
        }
    }

    public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
    {
        lock (_lock) Of(table).Remove((partitionKey, rowKey));
        return Task.CompletedTask;
    }

    public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var t = Of(table);
            foreach (var k in t.Between((partitionKey, ""), (partitionKey + "\0", "")).ToList()) t.Remove(k);
        }
        return Task.CompletedTask;
    }
}
