using System.Globalization;
using System.Text.Json;

namespace Craft.Services;

/// <summary>
/// A backend-neutral table row: a partition key, a row key, and a string→value property bag.
/// Values are the primitive types the host persists (string, int, DateTimeOffset, null).
///
/// The typed getters accept both native CLR values (as an Azure-Tables-style backend returns them)
/// and the shapes a JSON-column backend returns after a serialization round-trip (long/double/string/
/// <see cref="JsonElement"/>), so the same consumer code works regardless of the storage provider.
/// </summary>
public sealed class StoreRow
{
    public string PartitionKey { get; init; } = "";
    public string RowKey { get; init; } = "";
    public DateTimeOffset? Timestamp { get; init; }
    public Dictionary<string, object?> Properties { get; init; } = new();

    public StoreRow() { }

    public StoreRow(string partitionKey, string rowKey)
    {
        PartitionKey = partitionKey;
        RowKey = rowKey;
    }

    public object? this[string name]
    {
        get => Properties.TryGetValue(name, out var v) ? v : null;
        set => Properties[name] = value;
    }

    public string? GetString(string name) => this[name] switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null
    };

    public int? GetInt32(string name) => this[name] switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) => r,
        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var r) => r,
        JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) => r,
        _ => null
    };

    public DateTimeOffset? GetDateTimeOffset(string name) => this[name] switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var r) => r,
        JsonElement { ValueKind: JsonValueKind.String } je when je.TryGetDateTimeOffset(out var r) => r,
        _ => null
    };
}

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
