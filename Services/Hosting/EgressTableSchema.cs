using System.Globalization;
using System.Text.Json;
using Craft.Storage;

namespace Craft.Hosting;

/// <summary>
/// Shapes the egress rows that <see cref="EgressLedger"/> mirrors into the accounting table, and the
/// keys used to query them. Pure and side-effect-free so the layout can be unit-tested without a
/// storage backend.
/// <para>
/// Two row kinds share the table, distinguished by a RowKey prefix so each can be queried on its own:
/// <list type="bullet">
///   <item><description><b>Buckets</b> (<c>bkt_</c>) — one row per (15-min bucket, client) plus a
///   per-bucket instance aggregate. The time-series the product charts.</description></item>
///   <item><description><b>Daily summaries</b> (<c>day_</c>) — one row per (UTC day, client) plus a
///   per-day instance aggregate carrying the cap in force, when the cap was first hit that day, how many
///   requests were shed, and whether enforcement was on. The durable audit of "was the cap hit, when,
///   how many times, and what was it".</description></item>
/// </list>
/// <b>PartitionKey</b> is the client's AppId (GUID) for per-client rows, or <see cref="SystemPartition"/>
/// for the instance aggregate — so one client's history is a single-partition scan and the instance
/// trend is another. <b>RowKey</b> embeds a lexically-sortable UTC stamp after the prefix, so "last 24h"
/// and retention are range queries, never full scans. Azure Table key chars <c>/ \ # ?</c> are illegal,
/// so the prefix separator is <c>_</c>. The same storage account the hosted app reads with Get-CIPPTable
/// backs this table, so CIPP queries it directly rather than through Craft.
/// </para>
/// </summary>
internal static class EgressTableSchema
{
    /// <summary>Partition holding the instance aggregate. Not a GUID, so it never collides with an AppId.</summary>
    public const string SystemPartition = "instance-total";

    /// <summary>Partition holding interactive (user) traffic: tracked for reporting, never billed against the cap.</summary>
    public const string InteractivePartition = "interactive";

    public const string BucketPrefix = "bkt_";
    public const string DailyPrefix = "day_";
    // Upper bound of the bkt_ prefix range: 'u' is the next char after 't', so any bkt_* RowKey is < this.
    public const string BucketPrefixEnd = "bku_";
    public const string DailyPrefixEnd = "daz_";

    // Property names on the row — kept stable, CIPP reads these.
    public const string PropBytes = "Bytes";
    public const string PropRequests = "Requests";
    public const string PropShed = "Shed";
    public const string PropCapBytes = "CapBytes";
    public const string PropBucketStart = "BucketStartUtc";
    public const string PropDateUtc = "DateUtc";
    public const string PropCapReachedUtc = "CapReachedUtc";
    public const string PropEnforcing = "Enforcing";
    public const string PropAppId = "AppId";
    // JSON object of endpoint label -> [Bytes, Requests, MaxBytes, CacheHits, Errors, Shed]. Arrays rather
    // than named fields keep a full map under the 32K-char property limit; CIPP can't reassemble Craft splits.
    public const string PropEndpoints = "Endpoints";

    /// <summary>Endpoints kept per map; the rest fold into <see cref="OtherEndpoint"/>.</summary>
    public const int MaxEndpoints = 100;
    public const int MaxEndpointLabelLength = 96;
    public const string OtherEndpoint = "_other";
    public const string UnmatchedEndpoint = "_unmatched";

    /// <summary>The UTC start of the bucket <paramref name="utc"/> falls in, floored to
    /// <paramref name="bucketMinutes"/>.</summary>
    public static DateTime BucketStart(DateTime utc, int bucketMinutes)
    {
        var minutes = Math.Max(1, bucketMinutes);
        var day = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
        var flooredMinutes = ((long)(utc - day).TotalMinutes / minutes) * minutes;
        return day.AddMinutes(flooredMinutes);
    }

    private static string Stamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);

    private static string DayStamp(DateOnly day) =>
        day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string BucketRowKey(DateTime bucketStartUtc) => BucketPrefix + Stamp(bucketStartUtc);
    public static string DailyRowKey(DateOnly dayUtc) => DailyPrefix + DayStamp(dayUtc);

    /// <summary>Lower bound RowKey for a "last <paramref name="hours"/>h" bucket query.</summary>
    public static string BucketSinceRowKey(DateTime nowUtc, int hours, int bucketMinutes) =>
        BucketPrefix + Stamp(BucketStart(nowUtc.AddHours(-Math.Abs(hours)), bucketMinutes));

    /// <summary>Exclusive upper bound RowKey for purging buckets outside the retention window.</summary>
    public static string BucketRetentionCutoffRowKey(DateTime nowUtc, int retentionDays, int bucketMinutes) =>
        BucketPrefix + Stamp(BucketStart(nowUtc.AddDays(-Math.Max(1, retentionDays)), bucketMinutes));

    /// <summary>Exclusive upper bound RowKey for purging daily rows outside the retention window.</summary>
    public static string DailyRetentionCutoffRowKey(DateTime nowUtc, int retentionDays) =>
        DailyPrefix + DayStamp(DateOnly.FromDateTime(nowUtc.AddDays(-Math.Max(1, retentionDays))));

    public static StoreRow ClientBucketRow(string appId, DateTime bucketStartUtc,
        long bytes, long requests, long shed, long capBytes, IReadOnlyDictionary<string, EndpointStats>? endpoints = null)
    {
        var row = new StoreRow(appId, BucketRowKey(bucketStartUtc));
        FillCounts(row, bytes, requests, shed, capBytes, endpoints);
        row[PropBucketStart] = bucketStartUtc;
        row[PropAppId] = appId;
        return row;
    }

    public static StoreRow SystemBucketRow(DateTime bucketStartUtc,
        long bytes, long requests, long shed, long capBytes, IReadOnlyDictionary<string, EndpointStats>? endpoints = null)
    {
        var row = new StoreRow(SystemPartition, BucketRowKey(bucketStartUtc));
        FillCounts(row, bytes, requests, shed, capBytes, endpoints);
        row[PropBucketStart] = bucketStartUtc;
        return row;
    }

    public static StoreRow ClientDailyRow(string appId, DateOnly dayUtc,
        long bytes, long requests, long shed, long capBytes, IReadOnlyDictionary<string, EndpointStats>? endpoints = null)
    {
        var row = new StoreRow(appId, DailyRowKey(dayUtc));
        FillCounts(row, bytes, requests, shed, capBytes, endpoints);
        row[PropDateUtc] = dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        row[PropAppId] = appId;
        return row;
    }

    public static StoreRow SystemDailyRow(DateOnly dayUtc,
        long bytes, long requests, long shed, long capBytes, DateTime? capReachedUtc, bool enforcing,
        IReadOnlyDictionary<string, EndpointStats>? endpoints = null)
    {
        var row = new StoreRow(SystemPartition, DailyRowKey(dayUtc));
        FillCounts(row, bytes, requests, shed, capBytes, endpoints);
        row[PropDateUtc] = dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        row[PropEnforcing] = enforcing;
        if (capReachedUtc.HasValue) row[PropCapReachedUtc] = capReachedUtc.Value;
        return row;
    }

    private static void FillCounts(StoreRow row, long bytes, long requests, long shed, long capBytes,
        IReadOnlyDictionary<string, EndpointStats>? endpoints)
    {
        row[PropBytes] = bytes;
        row[PropRequests] = requests;
        row[PropShed] = shed;
        row[PropCapBytes] = capBytes;
        if (endpoints is { Count: > 0 }) row[PropEndpoints] = SerializeEndpoints(endpoints);
    }

    /// <summary>Clamps an endpoint label to a bounded, table-safe form; blank is <see cref="UnmatchedEndpoint"/>.</summary>
    public static string EndpointLabel(string? label)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return UnmatchedEndpoint;
        if (trimmed.Length > MaxEndpointLabelLength) trimmed = trimmed[..MaxEndpointLabelLength];
        return string.Create(trimmed.Length, trimmed, static (dst, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                dst[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '/' or '{' or '}' or '(' or ')' or '$' or '-' ? c : '_';
            }
        });
    }

    /// <summary>The top <see cref="MaxEndpoints"/> endpoints by bytes, the remainder folded into
    /// <see cref="OtherEndpoint"/>, as the compact JSON stored in <see cref="PropEndpoints"/>.</summary>
    public static string SerializeEndpoints(IReadOnlyDictionary<string, EndpointStats> endpoints)
    {
        var ordered = endpoints.Where(kv => kv.Key != OtherEndpoint).OrderByDescending(kv => kv.Value.Bytes).ToList();
        var other = endpoints.TryGetValue(OtherEndpoint, out var o) ? o.Clone() : null;
        foreach (var (_, stats) in ordered.Skip(MaxEndpoints))
            (other ??= new EndpointStats()).Merge(stats);

        var map = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var (label, stats) in ordered.Take(MaxEndpoints)) map[label] = stats.ToArray();
        if (other is not null) map[OtherEndpoint] = other.ToArray();
        return JsonSerializer.Serialize(map);
    }

    /// <summary>Parses <see cref="PropEndpoints"/>; anything unreadable is an empty map.</summary>
    public static Dictionary<string, EndpointStats> ParseEndpoints(string? json)
    {
        var result = new Dictionary<string, EndpointStats>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            foreach (var (label, values) in JsonSerializer.Deserialize<Dictionary<string, long[]>>(json) ?? [])
                result[label] = EndpointStats.FromArray(values);
        }
        catch (JsonException) { }
        return result;
    }
}

/// <summary>Per-endpoint counters within one client-day or one bucket.</summary>
internal sealed class EndpointStats
{
    public long Bytes, Requests, MaxBytes, CacheHits, Errors, Shed;

    public void Add(long bytes, bool cacheHit, int statusCode)
    {
        Bytes += bytes;
        Requests += 1;
        if (bytes > MaxBytes) MaxBytes = bytes;
        if (cacheHit) CacheHits += 1;
        if (statusCode >= 400) Errors += 1;
    }

    public void Merge(EndpointStats other)
    {
        Bytes += other.Bytes;
        Requests += other.Requests;
        MaxBytes = Math.Max(MaxBytes, other.MaxBytes);
        CacheHits += other.CacheHits;
        Errors += other.Errors;
        Shed += other.Shed;
    }

    public EndpointStats Clone() => (EndpointStats)MemberwiseClone();

    /// <summary>The entry for <paramref name="label"/>, created on first use; once the map is full, new
    /// labels share <see cref="EgressTableSchema.OtherEndpoint"/> so a caller can't grow it unbounded.</summary>
    public static EndpointStats In(Dictionary<string, EndpointStats> map, string label)
    {
        if (map.TryGetValue(label, out var stats)) return stats;
        if (map.Count >= EgressTableSchema.MaxEndpoints) label = EgressTableSchema.OtherEndpoint;
        if (!map.TryGetValue(label, out stats)) map[label] = stats = new EndpointStats();
        return stats;
    }

    public static Dictionary<string, EndpointStats> CloneMap(Dictionary<string, EndpointStats> map) =>
        map.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    public long[] ToArray() => [Bytes, Requests, MaxBytes, CacheHits, Errors, Shed];

    public static EndpointStats FromArray(long[]? v)
    {
        long At(int i) => v is not null && i < v.Length ? v[i] : 0;
        return new EndpointStats { Bytes = At(0), Requests = At(1), MaxBytes = At(2), CacheHits = At(3), Errors = At(4), Shed = At(5) };
    }
}
