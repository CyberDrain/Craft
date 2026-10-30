using Craft.Configuration;
using Craft.Endpoints;
using Craft.Orchestration;
using Craft.Storage;
using Cronos;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// End to end over simulated time: a real <see cref="SchedulerService"/> loads a timer file, resolves its
/// timezone from CraftTZ, resolves jitter from the site name and is ticked every 30 seconds, as the hosted
/// loop does, across whole days, a DST change, a restart and the warmup's runtime timezone switch. Only
/// dispatch is left out.
/// </summary>
public sealed class SchedulerTimezoneJitterE2ETests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private const string Site = "cippeln4y";

    private readonly string? _craftTz = Environment.GetEnvironmentVariable("CraftTZ");
    private readonly string? _siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
    private readonly string _timerFile = Path.Combine(Path.GetTempPath(), $"craft-timers-{Guid.NewGuid():N}.json");

    public SchedulerTimezoneJitterE2ETests()
    {
        Environment.SetEnvironmentVariable("CraftTZ", "Europe/London");
        Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", Site);
        File.WriteAllText(_timerFile, """
            [
              { "Id": "nightly", "Command": "Nightly", "Cron": "0 30 1 * * *", "JitterMinutes": 30 },
              { "Id": "two-hourly", "Command": "TwoHourly", "Cron": "0 45 */2 * * *" },
              { "Id": "flagged", "Command": "Flagged", "Cron": "0 0 4 * * *", "TZOffset": true }
            ]
            """);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CraftTZ", _craftTz);
        Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", _siteName);
        File.Delete(_timerFile);
    }

    private SchedulerService NewScheduler(bool applyTzOffset) => new(
        NullLogger<SchedulerService>.Instance, null!, null!, null!, null!,
        new CraftSettings { Scheduler = new SchedulerSettings { ConfigFile = _timerFile, ApplyTZOffset = applyTzOffset } },
        null!, null!, NativeScheduledTasks.Empty, null!);

    private static List<(string Command, DateTimeOffset At)> Run(SchedulerService scheduler, DateTimeOffset from, DateTimeOffset to)
    {
        var fired = new List<(string, DateTimeOffset)>();
        for (var now = from; now < to; now += Tick)
        {
            fired.AddRange(scheduler.CollectDue(now).Select(t => (t.Command, now)));
        }
        return fired;
    }

    private static DateTimeOffset Utc(int month, int day, int hour = 0, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static TimeSpan NightlyOffset() =>
        SchedulerService.JitterOffset(CronExpression.Parse("0 30 1 * * *", CronFormat.IncludeSeconds), London, 30,
            SchedulerService.JitterFraction(Site), DateTimeOffset.UtcNow, out _);

    [Fact]
    public void WithoutApplyTZOffsetOnlyFlaggedTimersFollowCraftTZ()
    {
        var scheduler = NewScheduler(applyTzOffset: false);
        scheduler.Start(Utc(10, 18));

        var fired = Run(scheduler, Utc(10, 18), Utc(10, 19));

        // BST is UTC+1: the flagged 04:00 timer fires at 03:00 UTC, the unflagged one stays on even UTC hours.
        Assert.Equal([Utc(10, 18, 3)], fired.Where(f => f.Command == "Flagged").Select(f => f.At));
        Assert.All(fired.Where(f => f.Command == "TwoHourly"), f => Assert.Equal(0, f.At.Hour % 2));
    }

    [Fact]
    public void WithApplyTZOffsetEveryTimerFollowsCraftTZ()
    {
        var scheduler = NewScheduler(applyTzOffset: true);
        scheduler.Start(Utc(10, 18));

        var fired = Run(scheduler, Utc(10, 18), Utc(10, 19));

        Assert.Equal([Utc(10, 18, 3)], fired.Where(f => f.Command == "Flagged").Select(f => f.At));
        Assert.Equal(12, fired.Count(f => f.Command == "TwoHourly"));
        Assert.All(fired.Where(f => f.Command == "TwoHourly"), f => Assert.Equal(0, TimeZoneInfo.ConvertTime(f.At, London).Hour % 2));
    }

    [Theory]
    [InlineData(3, 27)] // DST starts 03-29: 01:30 does not exist that night
    [InlineData(10, 23)] // DST ends 10-25: 01:30 happens twice that night
    public void AJitteredNightlyTimerFiresOncePerNightAtItsSlotPlusOffsetAcrossDst(int month, int day)
    {
        var scheduler = NewScheduler(applyTzOffset: true);
        var from = Utc(month, day);
        var to = from.AddDays(5);
        scheduler.Start(from);

        var fired = Run(scheduler, from, to).Where(f => f.Command == "Nightly").Select(f => f.At).ToList();

        var offset = NightlyOffset();
        var expected = CronExpression.Parse("0 30 1 * * *", CronFormat.IncludeSeconds).GetOccurrences(from, to, London).Select(s => s + offset).ToList();
        Assert.Equal(expected.Count, fired.Count);
        Assert.All(expected.Zip(fired), p => Assert.InRange(p.Second, p.First, p.First + Tick));
    }

    [Fact]
    public void ARestartInsideTheJitterWindowStillFiresTheNightlySlotOnce()
    {
        var offset = NightlyOffset();
        Assert.True(offset > TimeSpan.FromMinutes(2), "the site's offset must leave room for a restart inside it");
        var slot = Utc(10, 20, 0, 30); // 01:30 BST
        var restart = slot + offset / 2;

        var before = NewScheduler(applyTzOffset: true);
        before.Start(Utc(10, 20));
        var firedBefore = Run(before, Utc(10, 20), restart);

        var after = NewScheduler(applyTzOffset: true);
        after.Start(restart);
        var firedAfter = Run(after, restart, Utc(10, 21));

        Assert.DoesNotContain(firedBefore, f => f.Command == "Nightly");
        var nightly = Assert.Single(firedAfter, f => f.Command == "Nightly");
        Assert.InRange(nightly.At, slot + offset, slot + offset + Tick);
    }

    [Fact]
    public void ARestartAfterTheFiringDoesNotRepeatIt()
    {
        var slot = Utc(10, 20, 0, 30);
        var restart = slot + NightlyOffset() + TimeSpan.FromMinutes(1);

        var before = NewScheduler(applyTzOffset: true);
        before.Start(Utc(10, 20));
        Assert.Single(Run(before, Utc(10, 20), restart), f => f.Command == "Nightly");

        var after = NewScheduler(applyTzOffset: true);
        after.Start(restart);
        Assert.DoesNotContain(Run(after, restart, Utc(10, 21)), f => f.Command == "Nightly");
    }

    [Fact]
    public void TheWarmupsTimezoneSwitchMovesTimersWithoutDoubleFiring()
    {
        // The scheduler can start before the warmup has set CraftTZ; the warmup then calls SetTimezone.
        Environment.SetEnvironmentVariable("CraftTZ", null);
        var scheduler = NewScheduler(applyTzOffset: true);
        scheduler.Start(Utc(10, 20));
        var fired = Run(scheduler, Utc(10, 20), Utc(10, 20, 0, 10));

        scheduler.SetTimezone("Europe/London", Utc(10, 20, 0, 10));
        fired.AddRange(Run(scheduler, Utc(10, 20, 0, 10), Utc(10, 21)));

        var nightly = Assert.Single(fired, f => f.Command == "Nightly");
        Assert.InRange(nightly.At, Utc(10, 20, 0, 30) + NightlyOffset(), Utc(10, 20, 0, 30) + NightlyOffset() + Tick);
        Assert.Equal([Utc(10, 20, 3)], fired.Where(f => f.Command == "Flagged").Select(f => f.At));
    }

    [Fact]
    public async Task TheStateTableRecordsEachFiringAndTheNextWithItsJitter()
    {
        var store = new MemoryTableStore();
        await store.UpsertAsync("Timers", new StoreRow("Timer", "nightly") { ["LastFired"] = Utc(10, 17, 0, 40) });
        await store.UpsertAsync("Timers", new StoreRow("Timer", "removed"));
        var scheduler = new SchedulerService(NullLogger<SchedulerService>.Instance, null!, null!, null!, null!,
            new CraftSettings { Scheduler = new SchedulerSettings { ConfigFile = _timerFile, ApplyTZOffset = true, StateTable = "Timers" } },
            null!, null!, NativeScheduledTasks.Empty, null!, store);
        scheduler.Start(Utc(10, 18));

        await scheduler.LoadStateAsync(CancellationToken.None);

        // 01:30 BST is 00:30 UTC; a restart keeps the last firing and drops timers no longer in the file
        var nightly = await store.GetAsync("Timers", "Timer", "nightly");
        Assert.Equal(Utc(10, 17, 0, 40), nightly!.GetDateTimeOffset("LastFired"));
        Assert.Equal(Utc(10, 18, 0, 30) + NightlyOffset(), nightly.GetDateTimeOffset("NextFire"));
        Assert.Null(await store.GetAsync("Timers", "Timer", "removed"));

        var now = Utc(10, 18);
        List<SchedulerTask> due;
        while (!(due = scheduler.CollectDue(now)).Exists(t => t.Id == "nightly")) now += Tick;
        await scheduler.WriteStateAsync(due);

        nightly = await store.GetAsync("Timers", "Timer", "nightly");
        Assert.Equal(now, nightly!.GetDateTimeOffset("LastFired"));
        Assert.Equal(Utc(10, 19, 0, 30) + NightlyOffset(), nightly.GetDateTimeOffset("NextFire"));
    }
}
