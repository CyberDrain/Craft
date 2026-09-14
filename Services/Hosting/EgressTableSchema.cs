using System.Globalization;
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
        long bytes, long requests, long shed, long capBytes)
    {
        var row = new StoreRow(appId, BucketRowKey(bucketStartUtc));
        FillCounts(row, bytes, requests, shed, capBytes);
        row[PropBucketStart] = bucketStartUtc;
        row[PropAppId] = appId;
        return row;
    }

    public static StoreRow SystemBucketRow(DateTime bucketStartUtc,
        long bytes, long requests, long shed, long capBytes)
    {
        var row = new StoreRow(SystemPartition, BucketRowKey(bucketStartUtc));
        FillCounts(row, bytes, requests, shed, capBytes);
        row[PropBucketStart] = bucketStartUtc;
        return row;
    }

    public static StoreRow ClientDailyRow(string appId, DateOnly dayUtc,
        long bytes, long requests, long shed, long capBytes)
    {
        var row = new StoreRow(appId, DailyRowKey(dayUtc));
        FillCounts(row, bytes, requests, shed, capBytes);
        row[PropDateUtc] = dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        row[PropAppId] = appId;
        return row;
    }

    public static StoreRow SystemDailyRow(DateOnly dayUtc,
        long bytes, long requests, long shed, long capBytes, DateTime? capReachedUtc, bool enforcing)
    {
        var row = new StoreRow(SystemPartition, DailyRowKey(dayUtc));
        FillCounts(row, bytes, requests, shed, capBytes);
        row[PropDateUtc] = dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        row[PropEnforcing] = enforcing;
        if (capReachedUtc.HasValue) row[PropCapReachedUtc] = capReachedUtc.Value;
        return row;
    }

    private static void FillCounts(StoreRow row, long bytes, long requests, long shed, long capBytes)
    {
        row[PropBytes] = bytes;
        row[PropRequests] = requests;
        row[PropShed] = shed;
        row[PropCapBytes] = capBytes;
    }
}
