using Craft.Orchestration;
using Cronos;

namespace Craft.Tests;

/// <summary>
/// Per-timer jitter: every instance fires a stable share of each timer's window after the cron slot, so
/// apps on a shared plan stop starting the same work in the same minute.
/// </summary>
public class SchedulerJitterTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Day = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
    private static CronExpression Cron(string expression) => CronExpression.Parse(expression, CronFormat.IncludeSeconds);

    [Fact]
    public void TheFractionIsStablePerSiteAndInRange()
    {
        var first = SchedulerService.JitterFraction("cippeln4y");

        Assert.Equal(first, SchedulerService.JitterFraction("cippeln4y"));
        Assert.InRange(first, 0, 0.999999);
    }

    [Fact]
    public void SitesOnOnePlanSpreadAcrossTheWindow()
    {
        string[] sites =
        [
            "cipp2k6ry", "cipp2ssi2", "cipp4izxr", "cipp6irwz", "cipp72i6u", "cipp7kwhv", "cippa2jrv", "cippa2loq",
            "cippbms3r", "cippcalwg", "cippd3pks", "cippdcndp", "cippeln4y", "cippgfh3j", "cippjezl2", "cippjmx2d",
            "cippm2kfj", "cippmgbyo", "cippmkllb", "cippms5dw", "cippoqxif", "cipps2mjv", "cippugxus", "cippvafch", "cippz5uw3",
        ];

        var quintiles = sites.Select(s => (int)(SchedulerService.JitterFraction(s) * 5)).Distinct().Count();

        Assert.Equal(5, quintiles);
    }

    [Fact]
    public void TheOffsetIsTheSitesShareOfTheWindow()
    {
        var offset = SchedulerService.JitterOffset(Cron("0 30 1 * * *"), Utc, 30, 0.5, Day, out var clamped);

        Assert.Equal(TimeSpan.FromMinutes(15), offset);
        Assert.False(clamped);
    }

    [Fact]
    public void NoJitterFiresOnTheSlot()
    {
        Assert.Equal(TimeSpan.Zero, SchedulerService.JitterOffset(Cron("0 30 1 * * *"), Utc, 0, 0.9, Day, out _));
    }

    [Fact]
    public void AWindowAsLongAsTheCronPeriodIsClampedBelowIt()
    {
        var offset = SchedulerService.JitterOffset(Cron("0 */15 * * * *"), Utc, 20, 0.999, Day, out var clamped);

        Assert.True(clamped);
        Assert.True(offset < TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void AJitteredTimerWaitsForItsOffset()
    {
        var cron = Cron("0 30 1 * * *");
        var jitter = TimeSpan.FromMinutes(20);
        var lastRun = Day.AddHours(1);

        Assert.False(SchedulerService.IsDue(cron, Utc, lastRun, jitter, Day.AddMinutes(105)));
        Assert.True(SchedulerService.IsDue(cron, Utc, lastRun, jitter, Day.AddMinutes(110)));
    }

    [Fact]
    public void ARestartInsideTheWindowStillFiresTheSlot()
    {
        // Slot 01:30 with a 20 minute offset fires at 01:50; the process restarts at 01:40.
        var cron = Cron("0 30 1 * * *");
        var jitter = TimeSpan.FromMinutes(20);
        var startup = Day.AddMinutes(100);
        var seeded = startup - jitter;

        Assert.False(SchedulerService.IsDue(cron, Utc, seeded, jitter, Day.AddMinutes(105)));
        Assert.True(SchedulerService.IsDue(cron, Utc, seeded, jitter, Day.AddMinutes(110)));
    }

    [Fact]
    public void ARestartAfterTheFiringDoesNotFireTheSlotAgain()
    {
        var cron = Cron("0 30 1 * * *");
        var jitter = TimeSpan.FromMinutes(20);
        var startup = Day.AddMinutes(115);
        var seeded = startup - jitter;

        Assert.False(SchedulerService.IsDue(cron, Utc, seeded, jitter, Day.AddMinutes(116)));
        Assert.True(SchedulerService.IsDue(cron, Utc, seeded, jitter, Day.AddDays(1).AddMinutes(110)));
    }

    [Fact]
    public void TheNextSlotIsNotSkippedAfterAJitteredFiring()
    {
        var cron = Cron("0 */15 * * * *");
        var jitter = SchedulerService.JitterOffset(cron, Utc, 20, 0.999, Day, out _);
        var firedAt = Day + jitter;

        Assert.True(SchedulerService.IsDue(cron, Utc, firedAt, jitter, Day.AddMinutes(15) + jitter));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.37)]
    [InlineData(0.999)]
    public void ATimerWithAtLeastItsPredecessorsJitterNeverMovesCloserToIt(double fraction)
    {
        // Intune export 00:30 (5m) -> cache 01:30 (30m) -> tests 03:30 (30m)
        var export = Day.AddMinutes(30) + SchedulerService.JitterOffset(Cron("0 30 0 * * *"), Utc, 5, fraction, Day, out _);
        var cache = Day.AddMinutes(90) + SchedulerService.JitterOffset(Cron("0 30 1 * * *"), Utc, 30, fraction, Day, out _);
        var tests = Day.AddMinutes(210) + SchedulerService.JitterOffset(Cron("0 30 3 * * *"), Utc, 30, fraction, Day, out _);

        Assert.True(cache - export >= TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromHours(2), tests - cache);
    }
}
