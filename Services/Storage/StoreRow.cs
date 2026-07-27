using System.Globalization;
using System.Text.Json;

namespace Craft.Storage;

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
