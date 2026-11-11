using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>Every per-job entry the JobManager holds must go once the job is done, whichever way it ended.</summary>
public class JobManagerRetentionTests
{
    private const int N = 10_000;

    private static JobManager NewManager()
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = 8;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundBaseConcurrency"] = "8",
        }).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        return new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
    }

    private static Task Start(JobManager jobs) => Task.Run(() => jobs.StartAsync(CancellationToken.None));

    private static Dictionary<string, long> Counts(JobManager jobs)
    {
        var c = new Dictionary<string, long>();
        jobs.CountRetained(c);
        return c;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 1200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task SkippedJobs_AreCleanedUpLikeEveryOtherFinishedJob()
    {
        var jobs = NewManager();
        jobs.SetWorkResolver((_, _) => Task.FromResult<Func<CancellationToken, Task>?>(null));
        _ = Start(jobs);

        for (var i = 0; i < N; i++) jobs.Enqueue(new JobDescriptor("run", $"t{i}", 4), $"run-t{i}");
        await WaitUntilAsync(() => Counts(jobs).GetValueOrDefault("JobManager.jobs.Skipped") == N, "the stale descriptors were never skipped");

        foreach (var j in jobs.GetJobs()) j.CompletedUtc = DateTime.UtcNow.AddHours(-25);
        jobs.CleanupOldJobs();

        Assert.Equal(0, Counts(jobs)["JobManager.jobs"]);
    }

    [Fact]
    public async Task ReprioritizedJobs_LeaveNothingBehindOnceTheyRan()
    {
        var jobs = NewManager();
        var ran = 0;
        var ids = Enumerable.Range(0, N).Select(i => jobs.Enqueue($"j{i}", 5, _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; })).ToList();
        foreach (var id in ids) Assert.True(jobs.ChangePriority(id, 1));
        _ = Start(jobs);

        await WaitUntilAsync(() => ids.All(id => !jobs.IsQueuedOrRunning(id)), "the reprioritized jobs never finished");
        await Task.Delay(200);

        Assert.Equal(N, Volatile.Read(ref ran));
        Assert.Equal(0, Counts(jobs).GetValueOrDefault("JobManager.reprioritized"));
    }

    [Fact]
    public async Task ACancelledJob_DoesNotCancelItsReEnqueuedSuccessor()
    {
        var jobs = NewManager();
        var ran = 0;
        jobs.SetWorkResolver((_, _) => Task.FromResult<Func<CancellationToken, Task>?>(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }));
        for (var i = 0; i < N; i++)
        {
            var id = jobs.Enqueue(new JobDescriptor("run", $"t{i}", 5), $"run-t{i}");
            Assert.True(jobs.CancelJob(id));
            jobs.Enqueue(new JobDescriptor("run", $"t{i}", 1), $"run-t{i}");
        }
        _ = Start(jobs);

        await WaitUntilAsync(() => Enumerable.Range(0, N).All(i => !jobs.IsQueuedOrRunning($"run-t{i}")),
            "a re-enqueued job took its predecessor's cancellation and is reported queued forever");
        Assert.Equal(N, Volatile.Read(ref ran));
        Assert.Equal(0, Counts(jobs)["JobManager.cancelledJobs"]);
        Assert.Equal(0, Counts(jobs)["JobManager.pendingWork"]);
    }
}
