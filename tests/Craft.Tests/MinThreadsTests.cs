using Craft.Configuration;
using Craft.Hosting;

namespace Craft.Tests;

/// <summary>
/// The thread-pool minimum is the ceiling on how large a worker pool can usefully be.
/// <para>
/// PowerShell blocks a thread for the whole of every outbound call it makes, so N workers can park N
/// threads at once. Above the minimum the CLR injects threads at roughly one per second, which means
/// a pool larger than the minimum cannot reach its own concurrency until that ramp completes — on a
/// 1-core container with the old fixed floor of 32, a pool of 48 measured 5.5 req/s over 15 seconds
/// and 120 req/s over 60. Same configuration; only the ramp position differed.
/// </para>
/// </summary>
public class MinThreadsTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("CRAFT_MIN_THREADS");

    public MinThreadsTests() => Environment.SetEnvironmentVariable("CRAFT_MIN_THREADS", null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRAFT_MIN_THREADS", _original);
        GC.SuppressFinalize(this);
    }

    private static CraftSettings Settings(int http, int bg, int min = 0)
    {
        var settings = new CraftSettings();
        settings.Worker.HttpPoolSize = http;
        settings.Worker.BgPoolSize = bg;
        settings.Worker.MinThreads = min;
        return settings;
    }

    [Fact]
    public void SmallPoolsKeepTheHistoricalFloor()
    {
        // The old behaviour was max(cores*4, 32). Nothing should regress below it — that floor also
        // covers Kestrel and the storage SDK on a machine with no PowerShell load at all.
        var expected = Math.Max(Environment.ProcessorCount * 4, 32);

        Assert.Equal(expected, CraftHostBuilderExtensions.ResolveMinThreads(Settings(2, 4)));
    }

    [Fact]
    public void LargePoolsRaiseTheMinimumAboveTheCoreBasedFloor()
    {
        // The regression this whole setting exists for: on one core the old floor was 32, so a pool
        // of 48 spent its first ~16 seconds waiting for thread injection on every restart.
        var resolved = CraftHostBuilderExtensions.ResolveMinThreads(Settings(48, 0));

        Assert.True(resolved >= 48,
            $"a 48-worker pool resolved to {resolved} minimum threads — every worker can park a " +
            "thread, so a minimum below the pool size guarantees an injection ramp on startup.");
    }

    [Fact]
    public void BothPoolsCount()
    {
        // HTTP and background workers draw on the same thread pool.
        var httpOnly = CraftHostBuilderExtensions.ResolveMinThreads(Settings(40, 0));
        var both = CraftHostBuilderExtensions.ResolveMinThreads(Settings(40, 40));

        Assert.True(both > httpOnly, "background workers block threads too and must be counted");
        Assert.True(both >= 80, $"40 HTTP + 40 BG workers resolved to only {both}");
    }

    [Fact]
    public void HeadroomIsLeftAboveThePools()
    {
        // Kestrel, timers and the storage SDK need threads that are not parked in PowerShell.
        var pools = 64;
        var resolved = CraftHostBuilderExtensions.ResolveMinThreads(Settings(pools, 0));

        Assert.True(resolved >= pools + 16,
            $"{pools} workers resolved to {resolved}; the runtime's own work needs headroom above " +
            "the pool or it contends with parked PowerShell threads.");
    }

    [Fact]
    public void ExplicitSettingWins()
    {
        Assert.Equal(500, CraftHostBuilderExtensions.ResolveMinThreads(Settings(4, 4, min: 500)));
    }

    [Fact]
    public void ExplicitSettingCanGoBelowTheDerivedValue()
    {
        // Pinning it low is a legitimate (if unusual) choice — an operator who has measured their
        // workload should be able to override the heuristic in both directions.
        var settings = Settings(64, 64, min: 40);

        Assert.Equal(40, CraftHostBuilderExtensions.ResolveMinThreads(settings));
    }

    [Fact]
    public void EnvironmentVariableOverridesEverything()
    {
        Environment.SetEnvironmentVariable("CRAFT_MIN_THREADS", "300");

        Assert.Equal(300, CraftHostBuilderExtensions.ResolveMinThreads(Settings(4, 4, min: 100)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void UnusableEnvironmentValuesFallThrough(string value)
    {
        // A malformed override must not silently pin the minimum at zero — that would be strictly
        // worse than the default it replaced.
        Environment.SetEnvironmentVariable("CRAFT_MIN_THREADS", value);

        Assert.Equal(Math.Max(Environment.ProcessorCount * 4, 32),
            CraftHostBuilderExtensions.ResolveMinThreads(Settings(2, 4)));
    }
}
