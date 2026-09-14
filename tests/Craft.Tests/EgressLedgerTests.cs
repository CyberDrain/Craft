using System.Globalization;
using System.Text.Json;
using Craft.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The egress ledger is the instance-wide byte counter behind the daily API bandwidth cap. Its
/// correctness is what makes the cap trustworthy in both directions: under-counting lets an abuser past
/// the budget, over-counting throttles a well-behaved caller. These pin the behaviours that matter — it
/// accumulates, it rejects exactly at the budget, it resets at UTC midnight, it survives a restart within
/// the same day without resetting (John's hard requirement) while starting fresh on a new day, and it
/// keeps per-client totals + a shed count + the cap-reached stamp in the persisted file. The clock is
/// injected so rollover is testable without waiting for midnight. Table mirroring is covered separately
/// in <see cref="EgressLedgerTableTests"/>.
/// </summary>
public class EgressLedgerTests : IDisposable
{
    private const string App = "11111111-2222-3333-4444-555555555555";
    private const string App2 = "99999999-8888-7777-6666-555555555555";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "craft-egress-test-" + Guid.NewGuid().ToString("N")[..8]);

    private string FilePath => Path.Combine(_dir, "egress-ledger.json");

    public EgressLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private EgressLedger New(long cap, Func<DateTime>? clock = null) =>
        new(NullLogger<EgressLedger>.Instance, cap, flushSeconds: 60, FilePath, clock);

    // ── Accounting + enforcement ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Record_Accumulates()
    {
        var ledger = New(cap: 0);
        ledger.Record(100, App);
        ledger.Record(250, App);
        Assert.Equal(350L, ledger.CurrentBytes);
    }

    [Fact]
    public void Record_IgnoresZeroAndNegative()
    {
        var ledger = New(cap: 0);
        ledger.Record(0, App);
        ledger.Record(-99, App);
        Assert.Equal(0L, ledger.CurrentBytes);
    }

    [Fact]
    public void ShouldReject_FalseBelowCap_TrueAtOrAboveCap()
    {
        var ledger = New(cap: 1000);
        ledger.Record(999, App);
        Assert.False(ledger.ShouldReject());   // one byte under

        ledger.Record(1, App);
        Assert.True(ledger.ShouldReject());    // exactly at the cap rejects

        ledger.Record(5000, App);
        Assert.True(ledger.ShouldReject());    // and stays rejecting past it
    }

    [Fact]
    public void CapZero_IsAccountingOnly_NeverRejects()
    {
        var ledger = New(cap: 0);
        ledger.Record(long.MaxValue / 2, App);
        Assert.False(ledger.ShouldReject());   // accounting-only rollout phase: counts, never 429s
        Assert.True(ledger.CurrentBytes > 0);
    }

    // ── Per-client totals + shed count + cap-reached stamp (persisted) ────────────────────────────────

    [Fact]
    public void Record_TracksPerClientTotals_InFile()
    {
        var ledger = New(cap: 0);
        ledger.Record(100, App);
        ledger.Record(250, App);
        ledger.Record(400, App2);
        ledger.Flush();

        var state = ReadFile();
        Assert.Equal(750L, state.RootElement.GetProperty("bytes").GetInt64());
        var clients = state.RootElement.GetProperty("clients");
        Assert.Equal(350L, clients.GetProperty(App).GetProperty("bytes").GetInt64());
        Assert.Equal(2, clients.GetProperty(App).GetProperty("requests").GetInt32());
        Assert.Equal(400L, clients.GetProperty(App2).GetProperty("bytes").GetInt64());
    }

    [Fact]
    public void RecordShed_CountsAndStampsCapReached()
    {
        var clock = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var ledger = New(cap: 1000, () => clock);
        ledger.RecordShed(App);
        ledger.RecordShed(App);
        ledger.Flush();

        Assert.Equal(2L, ledger.ShedRequests);
        var state = ReadFile();
        Assert.Equal(2L, state.RootElement.GetProperty("shedRequests").GetInt64());
        Assert.Equal(2, state.RootElement.GetProperty("clients").GetProperty(App).GetProperty("shed").GetInt32());
        Assert.True(state.RootElement.TryGetProperty("capReachedUtc", out var cr) && cr.ValueKind != JsonValueKind.Null);
        Assert.Equal(1000L, state.RootElement.GetProperty("capBytes").GetInt64());
    }

    [Fact]
    public void PerClientTotals_RestoredAcrossRestart_SameDay()
    {
        var clock = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);
        var first = New(cap: 0, () => clock);
        first.Record(111, App);
        first.Record(222, App2);
        first.RecordShed(App);
        first.Flush();

        var restarted = New(cap: 0, () => clock);
        restarted.Flush();   // re-persist the loaded state
        var state = ReadFile();
        Assert.Equal(333L, restarted.CurrentBytes);
        Assert.Equal(1L, restarted.ShedRequests);
        Assert.Equal(111L, state.RootElement.GetProperty("clients").GetProperty(App).GetProperty("bytes").GetInt64());
    }

    // ── UTC midnight rollover ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rollover_ResetsCounterOnNewUtcDay()
    {
        var clock = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var ledger = New(cap: 1000, () => clock);

        ledger.Record(900, App);
        Assert.Equal(900L, ledger.CurrentBytes);

        clock = clock.AddDays(1); // cross UTC midnight
        Assert.Equal(0L, ledger.CurrentBytes);      // reads roll the day over
        Assert.False(ledger.ShouldReject());
        ledger.Record(50, App);
        Assert.Equal(50L, ledger.CurrentBytes);     // new day counts from zero
    }

    [Fact]
    public void SecondsToNextUtcMidnight_IsTimeUntilMidnight()
    {
        var clock = new DateTime(2026, 9, 11, 23, 0, 0, DateTimeKind.Utc);
        var ledger = New(cap: 0, () => clock);
        Assert.Equal(3600, ledger.SecondsToNextUtcMidnight());
    }

    [Fact]
    public void SecondsToNextUtcMidnight_FlooredAtOne()
    {
        var clock = new DateTime(2026, 9, 11, 23, 59, 59, 900, DateTimeKind.Utc);
        var ledger = New(cap: 0, () => clock);
        Assert.Equal(1, ledger.SecondsToNextUtcMidnight());
    }

    [Fact]
    public void NextUtcMidnightIso_IsTomorrowMidnightZulu()
    {
        var clock = new DateTime(2026, 9, 11, 8, 30, 0, DateTimeKind.Utc);
        var ledger = New(cap: 0, () => clock);
        Assert.Equal("2026-09-12T00:00:00Z", ledger.NextUtcMidnightIso());
    }

    // ── Persistence: survive restart within the day, reset across days ─────────────────────────────

    [Fact]
    public void Flush_ThenReload_RestoresSameDayTotal()
    {
        var clock = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);

        var first = New(cap: 5000, () => clock);
        first.Record(1234, App);
        first.Flush();

        var restarted = New(cap: 5000, () => clock);
        Assert.Equal(1234L, restarted.CurrentBytes);
    }

    [Fact]
    public void Reload_OnNewUtcDay_StartsFresh()
    {
        var day1 = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);
        var first = New(cap: 5000, () => day1);
        first.Record(1234, App);
        first.Flush();

        var day2 = day1.AddDays(1);
        var restarted = New(cap: 5000, () => day2);
        Assert.Equal(0L, restarted.CurrentBytes);   // yesterday's total is not carried into today
    }

    [Fact]
    public void Load_V1File_RestoresBytes()
    {
        // A pre-per-client (v1) file has only dateUtc + bytes. It must still load.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(FilePath, $"{{\"dateUtc\":\"{today}\",\"bytes\":4242}}");
        var ledger = New(cap: 0);
        Assert.Equal(4242L, ledger.CurrentBytes);
    }

    [Fact]
    public void Flush_SkippedWhenNothingChanged()
    {
        var ledger = New(cap: 1000);
        ledger.Flush();                         // nothing recorded → nothing dirty
        Assert.False(File.Exists(FilePath));    // an idle instance writes no file at all
    }

    [Fact]
    public void Flush_LeavesNoTempFileBehind()
    {
        var ledger = New(cap: 1000);
        ledger.Record(10, App);
        ledger.Flush();
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));   // temp+rename completed cleanly
    }

    [Fact]
    public void Load_CorruptFile_StartsFreshWithoutThrowing()
    {
        File.WriteAllText(FilePath, "this is not json {");
        var ledger = New(cap: 1000);            // must not throw
        Assert.Equal(0L, ledger.CurrentBytes);
    }

    [Fact]
    public void Load_NegativeStoredBytes_Ignored()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(FilePath, $"{{\"dateUtc\":\"{today}\",\"bytes\":-500}}");
        var ledger = New(cap: 1000);
        Assert.Equal(0L, ledger.CurrentBytes);   // a corrupt/negative total can't unlock a false budget
    }

    // ── Thread-safety + hosted-service lifecycle ────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentRecords_SumExactly()
    {
        // 56k+ requests/day can land concurrently on the shared counter; the lock must lose none.
        var ledger = New(cap: 0);
        const int threads = 200, perThread = 500;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++) ledger.Record(7, App);
        })));

        Assert.Equal((long)threads * perThread * 7, ledger.CurrentBytes);
    }

    [Fact]
    public async Task HostedService_FlushesOnShutdown()
    {
        var clock = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);
        var ledger = New(cap: 5000, () => clock);
        ledger.Record(2222, App);

        await ((IHostedService)ledger).StartAsync(CancellationToken.None);
        await ((IHostedService)ledger).StopAsync(CancellationToken.None); // triggers the final flush

        var reloaded = New(cap: 5000, () => clock);
        Assert.Equal(2222L, reloaded.CurrentBytes);
    }

    private JsonDocument ReadFile() => JsonDocument.Parse(File.ReadAllText(FilePath));
}
