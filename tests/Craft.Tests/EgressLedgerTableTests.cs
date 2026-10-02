using System.Globalization;
using Craft.Hosting;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The table mirror is what lets the product show egress over time and whether/when/how often the cap
/// was hit. These pin its shape and the two resilience behaviours: per-client + instance rows are written
/// for both the 15-minute bucket and the daily audit; a restart mid-bucket reseeds from the table so the
/// bucket row does not regress; and rows past the retention window are purged. Uses an in-memory store —
/// the real byte counting end to end is the perf-harness's job.
/// </summary>
public class EgressLedgerTableTests : IDisposable
{
    private const string App = "11111111-2222-3333-4444-555555555555";
    private const string App2 = "99999999-8888-7777-6666-555555555555";
    private const string Table = "CraftEgressAccounting";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "craft-egress-tbl-" + Guid.NewGuid().ToString("N")[..8]);
    private string FilePath => Path.Combine(_dir, "egress-ledger.json");

    public EgressLedgerTableTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private EgressLedger New(FakeTableStore store, long cap, Func<DateTime> clock, int retentionDays = 7) =>
        new(NullLogger<EgressLedger>.Instance, cap, flushSeconds: 60, FilePath, clock, store, Table,
            bucketMinutes: 15, retentionDays: retentionDays);

    private static long Long(StoreRow r, string prop) => Convert.ToInt64(r[prop] ?? 0L, CultureInfo.InvariantCulture);

    [Fact]
    public async Task SyncToTable_WritesPerClientAndSystem_ForBucketAndDaily()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 14, 22, 0, DateTimeKind.Utc);
        var ledger = New(store, cap: 1_000_000, () => clock);

        ledger.Record(100, App);
        ledger.Record(250, App);
        ledger.Record(400, App2);
        await ledger.SyncToTableAsync(CancellationToken.None);

        var rows = store.All(Table);

        // Bucket rows: per client + a system aggregate, all in the same 15-min bucket (bkt_...T141500Z).
        var appBucket = rows.Single(r => r.PartitionKey == App && r.RowKey.StartsWith(EgressTableSchema.BucketPrefix, StringComparison.Ordinal));
        Assert.Equal(350L, Long(appBucket, EgressTableSchema.PropBytes));
        Assert.Equal(2L, Long(appBucket, EgressTableSchema.PropRequests));
        Assert.EndsWith("T141500Z", appBucket.RowKey);   // floored to the 14:15 bucket

        Assert.Equal(400L, Long(rows.Single(r => r.PartitionKey == App2 && r.RowKey.StartsWith(EgressTableSchema.BucketPrefix, StringComparison.Ordinal)), EgressTableSchema.PropBytes));

        var sysBucket = rows.Single(r => r.PartitionKey == EgressTableSchema.SystemPartition && r.RowKey.StartsWith(EgressTableSchema.BucketPrefix, StringComparison.Ordinal));
        Assert.Equal(750L, Long(sysBucket, EgressTableSchema.PropBytes));   // aggregate == sum of clients

        // Daily rows: per client + a system summary carrying the cap.
        var appDaily = rows.Single(r => r.PartitionKey == App && r.RowKey.StartsWith(EgressTableSchema.DailyPrefix, StringComparison.Ordinal));
        Assert.Equal(350L, Long(appDaily, EgressTableSchema.PropBytes));
        var sysDaily = rows.Single(r => r.PartitionKey == EgressTableSchema.SystemPartition && r.RowKey.StartsWith(EgressTableSchema.DailyPrefix, StringComparison.Ordinal));
        Assert.Equal(750L, Long(sysDaily, EgressTableSchema.PropBytes));
        Assert.Equal(1_000_000L, Long(sysDaily, EgressTableSchema.PropCapBytes));
        Assert.Equal(true, sysDaily[EgressTableSchema.PropEnforcing]);
    }

    [Fact]
    public async Task SystemDailyRow_CarriesShedCountAndCapReached()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc);
        var ledger = New(store, cap: 1000, () => clock);

        ledger.RecordShed(App);
        ledger.RecordShed(App);
        await ledger.SyncToTableAsync(CancellationToken.None);

        var sysDaily = store.All(Table).Single(r => r.PartitionKey == EgressTableSchema.SystemPartition && r.RowKey.StartsWith(EgressTableSchema.DailyPrefix, StringComparison.Ordinal));
        Assert.Equal(2L, Long(sysDaily, EgressTableSchema.PropShed));
        Assert.NotNull(sysDaily[EgressTableSchema.PropCapReachedUtc]);
        Assert.Equal(2L, Long(store.All(Table).Single(r => r.PartitionKey == App && r.RowKey.StartsWith(EgressTableSchema.DailyPrefix, StringComparison.Ordinal)), EgressTableSchema.PropShed));
    }

    [Fact]
    public async Task Backfill_SeedsCurrentBucket_SoRestartDoesNotRegress()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 14, 22, 0, DateTimeKind.Utc);

        // A previous instance already wrote 500 bytes for App into the current bucket.
        var bucketStart = EgressTableSchema.BucketStart(clock, 15);
        await store.UpsertAsync(Table, EgressTableSchema.ClientBucketRow(App, bucketStart, 500, 3, 0, 1000), CancellationToken.None);

        // Restart: new ledger, backfill, then serve 100 more bytes.
        var ledger = New(store, cap: 1000, () => clock);
        await ledger.SeedCurrentBucketAsync(CancellationToken.None);
        ledger.Record(100, App);
        await ledger.SyncToTableAsync(CancellationToken.None);

        var appBucket = store.All(Table).Single(r => r.PartitionKey == App && r.RowKey.StartsWith(EgressTableSchema.BucketPrefix, StringComparison.Ordinal));
        Assert.Equal(600L, Long(appBucket, EgressTableSchema.PropBytes));   // 500 seeded + 100, not 100
    }

    [Fact]
    public async Task Purge_RemovesRowsBeyondRetention_KeepsRecent()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

        // Old rows (10 days ago) — beyond a 7-day window.
        var oldStart = EgressTableSchema.BucketStart(clock.AddDays(-10), 15);
        await store.UpsertAsync(Table, EgressTableSchema.ClientBucketRow(App, oldStart, 111, 1, 0, 1000), CancellationToken.None);
        await store.UpsertAsync(Table, EgressTableSchema.ClientDailyRow(App, DateOnly.FromDateTime(clock.AddDays(-10)), 111, 1, 0, 1000), CancellationToken.None);
        // Recent rows (now).
        var nowStart = EgressTableSchema.BucketStart(clock, 15);
        await store.UpsertAsync(Table, EgressTableSchema.ClientBucketRow(App, nowStart, 222, 1, 0, 1000), CancellationToken.None);
        await store.UpsertAsync(Table, EgressTableSchema.ClientDailyRow(App, DateOnly.FromDateTime(clock), 222, 1, 0, 1000), CancellationToken.None);

        Assert.Equal(4, store.Count(Table));

        var ledger = New(store, cap: 1000, () => clock, retentionDays: 7);
        await ledger.PurgeAsync(CancellationToken.None);

        var remaining = store.All(Table);
        Assert.Equal(2, remaining.Count);                                   // the two old rows are gone
        Assert.All(remaining, r => Assert.Contains("20260913", r.RowKey));   // only today's rows survive
    }

    // ── Endpoint breakdown + interactive partition ──────────────────────────────────────────────────

    private static StoreRow Row(FakeTableStore store, string partition, string prefix) =>
        store.All(Table).Single(r => r.PartitionKey == partition && r.RowKey.StartsWith(prefix, StringComparison.Ordinal));

    private static Dictionary<string, EndpointStats> Endpoints(StoreRow row) =>
        EgressTableSchema.ParseEndpoints(row.GetString(EgressTableSchema.PropEndpoints));

    [Fact]
    public async Task Endpoints_BrokenOutOnDailyRows_AndInstanceBucket_NotClientBuckets()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 14, 22, 0, DateTimeKind.Utc);
        var ledger = New(store, cap: 1_000_000, () => clock);

        ledger.Record(100, App, "ListUsers", cacheHit: true);
        ledger.Record(300, App, "ListUsers");
        ledger.Record(50, App2, "ListGraphRequest:users", statusCode: 500);
        ledger.RecordShed(App2, "ListUsers");
        await ledger.SyncToTableAsync(CancellationToken.None);

        var users = Endpoints(Row(store, App, EgressTableSchema.DailyPrefix))["ListUsers"];
        Assert.Equal([400L, 2, 300, 1, 0, 0], users.ToArray());   // Bytes, Requests, MaxBytes, CacheHits, Errors, Shed

        var instanceDay = Endpoints(Row(store, EgressTableSchema.SystemPartition, EgressTableSchema.DailyPrefix));
        Assert.Equal(1L, instanceDay["ListUsers"].Shed);
        Assert.Equal(1L, instanceDay["ListGraphRequest:users"].Errors);

        var instanceBucket = Endpoints(Row(store, EgressTableSchema.SystemPartition, EgressTableSchema.BucketPrefix));
        Assert.Equal(400L, instanceBucket["ListUsers"].Bytes);
        Assert.Equal(50L, instanceBucket["ListGraphRequest:users"].Bytes);

        Assert.Null(Row(store, App, EgressTableSchema.BucketPrefix)[EgressTableSchema.PropEndpoints]);
    }

    [Fact]
    public async Task Interactive_ReportedInOwnPartition_NeverBilled()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 14, 22, 0, DateTimeKind.Utc);
        var ledger = New(store, cap: 1000, () => clock);

        ledger.RecordInteractive(5000, "ListLogs");
        ledger.Record(100, App, "ListUsers");
        await ledger.SyncToTableAsync(CancellationToken.None);

        Assert.Equal(100L, ledger.CurrentBytes);
        Assert.False(ledger.ShouldReject());

        Assert.Equal(5000L, Endpoints(Row(store, EgressTableSchema.InteractivePartition, EgressTableSchema.DailyPrefix))["ListLogs"].Bytes);
        Assert.Equal(5000L, Endpoints(Row(store, EgressTableSchema.InteractivePartition, EgressTableSchema.BucketPrefix))["ListLogs"].Bytes);

        var instanceDay = Row(store, EgressTableSchema.SystemPartition, EgressTableSchema.DailyPrefix);
        Assert.Equal(100L, Long(instanceDay, EgressTableSchema.PropBytes));
        Assert.Equal(1L, Long(instanceDay, EgressTableSchema.PropRequests));
        Assert.DoesNotContain("ListLogs", Endpoints(instanceDay).Keys);
        Assert.Equal(100L, Long(Row(store, EgressTableSchema.SystemPartition, EgressTableSchema.BucketPrefix), EgressTableSchema.PropBytes));
    }

    [Fact]
    public async Task InstanceDailyRequests_CountClientsIdleSinceAnEarlierBucket()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Utc);
        var ledger = New(store, cap: 1_000_000, () => clock);

        ledger.Record(100, App, "ListUsers");
        await ledger.SyncToTableAsync(CancellationToken.None);
        clock = clock.AddMinutes(30);
        ledger.Record(100, App2, "ListUsers");
        await ledger.SyncToTableAsync(CancellationToken.None);

        var instanceDay = Row(store, EgressTableSchema.SystemPartition, EgressTableSchema.DailyPrefix);
        Assert.Equal(2L, Long(instanceDay, EgressTableSchema.PropRequests));
        Assert.Equal(2L, Endpoints(instanceDay)["ListUsers"].Requests);
    }

    [Fact]
    public async Task Restart_KeepsDailyEndpoints_FromFile_AndBucketEndpoints_FromTable()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 14, 22, 0, DateTimeKind.Utc);
        var first = New(store, cap: 1_000_000, () => clock);
        first.Record(100, App, "ListUsers");
        first.RecordInteractive(70, "ListLogs");
        first.Flush();
        await first.SyncToTableAsync(CancellationToken.None);

        var second = New(store, cap: 1_000_000, () => clock);
        await second.SeedCurrentBucketAsync(CancellationToken.None);
        second.Record(10, App, "ListUsers");
        second.RecordInteractive(5, "ListLogs");
        await second.SyncToTableAsync(CancellationToken.None);

        Assert.Equal(110L, Endpoints(Row(store, App, EgressTableSchema.DailyPrefix))["ListUsers"].Bytes);
        Assert.Equal(75L, Endpoints(Row(store, EgressTableSchema.InteractivePartition, EgressTableSchema.DailyPrefix))["ListLogs"].Bytes);
        Assert.Equal(110L, Endpoints(Row(store, EgressTableSchema.SystemPartition, EgressTableSchema.BucketPrefix))["ListUsers"].Bytes);
        Assert.Equal(75L, Endpoints(Row(store, EgressTableSchema.InteractivePartition, EgressTableSchema.BucketPrefix))["ListLogs"].Bytes);
    }

    [Fact]
    public async Task EndpointMap_IsBounded_AndFitsOneTableProperty()
    {
        var store = new FakeTableStore();
        var clock = new DateTime(2026, 9, 13, 14, 22, 0, DateTimeKind.Utc);
        var ledger = New(store, cap: 1_000_000, () => clock);

        for (var i = 0; i < 500; i++)
            ledger.Record(1_000_000_000_000 + i, App, $"Endpoint{i:D3}:" + new string('x', 200));
        await ledger.SyncToTableAsync(CancellationToken.None);

        var row = Row(store, App, EgressTableSchema.DailyPrefix);
        var map = Endpoints(row);
        Assert.Equal(EgressTableSchema.MaxEndpoints + 1, map.Count);
        Assert.Equal(500L - EgressTableSchema.MaxEndpoints, map[EgressTableSchema.OtherEndpoint].Requests);
        Assert.All(map.Keys, k => Assert.True(k.Length <= EgressTableSchema.MaxEndpointLabelLength));
        Assert.True(row.GetString(EgressTableSchema.PropEndpoints)!.Length < 32_000);
    }

    [Fact]
    public void EndpointLabel_ReplacesUnsafeCharacters_AndDefaultsBlank()
    {
        Assert.Equal("ListGraphRequest:deviceManagement/managedDevices({id})/$count", EgressTableSchema.EndpointLabel("ListGraphRequest:deviceManagement/managedDevices({id})/$count"));
        Assert.Equal("Exec__x_", EgressTableSchema.EndpointLabel("Exec\"<x>"));
        Assert.Equal(EgressTableSchema.UnmatchedEndpoint, EgressTableSchema.EndpointLabel("  "));
    }
}
