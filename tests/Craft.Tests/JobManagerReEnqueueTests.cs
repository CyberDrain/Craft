using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Descriptor jobs are keyed by a STABLE id — "{runName}-{taskId}" — unlike closure jobs, which get a
/// GUID suffix. That makes re-enqueueing the same task a collision, and <c>_jobs.TryAdd</c> resolves a
/// collision by keeping the OLD record and discarding the new one. The new work still runs: it goes on
/// the pending queue regardless, and the dispatcher writes status onto the queue item's own record.
///
/// So after a task has run once, the manager's tracked record for it is frozen at "Completed" while a
/// fresh copy of that job is queued or running. <c>IsQueuedOrRunning</c> reads the frozen record and
/// answers "no" for a job that is very much running.
///
/// That answer is load-bearing. JobQueuePump.ReleaseFinishedAsync treats it as "this job is done" and
/// DELETES the task's durable queue row. Observed live: the pump released 7-9 "finished" jobs every
/// second while only 8 could physically be running, on a run where individual tasks executed up to five
/// times.
/// </summary>
public class JobManagerReEnqueueTests
{
    private static JobManager NewManager(int concurrency = 2)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = concurrency;
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        return new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
    }

    /// <summary>
    /// StartAsync runs ExecuteAsync inline, so it must not be awaited from the test thread.
    /// </summary>
    private static Task Start(JobManager jobs) => Task.Run(() => jobs.StartAsync(CancellationToken.None));

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task ATaskEnqueuedAgainAfterItRan_IsReportedAsRunning_NotAsItsPreviousCompletion()
    {
        var jobs = NewManager();
        var ran = 0;
        var release = new SemaphoreSlim(0);

        jobs.SetWorkResolver((descriptor, _) => Task.FromResult<Func<CancellationToken, Task>?>(
            async _ =>
            {
                Interlocked.Increment(ref ran);
                await release.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }));

        _ = Start(jobs);

        // First outing: run it to completion.
        var id = jobs.Enqueue(new JobDescriptor("run", "task-0", 4), "run-task-0");
        await WaitUntilAsync(() => Volatile.Read(ref ran) == 1, "first job never started");
        release.Release();
        await WaitUntilAsync(() => !jobs.IsQueuedOrRunning(id), "first job never finished");

        // Second outing: the same task, same stable id. This is what the pump does every time it
        // claims that row again.
        var id2 = jobs.Enqueue(new JobDescriptor("run", "task-0", 4), "run-task-0");
        Assert.Equal(id, id2);

        await WaitUntilAsync(() => Volatile.Read(ref ran) == 2, "second job never started");

        // The job IS running. Answering "no" here is what makes the pump delete a live job's queue row.
        Assert.True(jobs.IsQueuedOrRunning(id),
            "the manager reports a running job as finished — its record is frozen at the previous run's " +
            "status, so JobQueuePump.ReleaseFinishedAsync will drop the durable row out from under it");

        release.Release();
        await jobs.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The same collision seen from the queue side: a re-enqueued task must not look finished the
    /// moment it is queued, or its row is released before it has run at all.
    /// </summary>
    [Fact]
    public async Task ATaskQueuedAgain_IsNotReportedAsFinishedWhileItWaits()
    {
        var jobs = NewManager(concurrency: 1);
        var started = 0;
        var release = new SemaphoreSlim(0);

        jobs.SetWorkResolver((descriptor, _) => Task.FromResult<Func<CancellationToken, Task>?>(
            async _ =>
            {
                Interlocked.Increment(ref started);
                await release.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }));

        _ = Start(jobs);

        var id = jobs.Enqueue(new JobDescriptor("run", "task-0", 4), "run-task-0");
        await WaitUntilAsync(() => Volatile.Read(ref started) == 1, "first job never started");
        release.Release();
        await WaitUntilAsync(() => !jobs.IsQueuedOrRunning(id), "first job never finished");

        // Concurrency is 1 and nothing is holding a slot, so this may start immediately — the point is
        // that between Enqueue returning and the work finishing it must never read as finished.
        jobs.Enqueue(new JobDescriptor("run", "task-0", 4), "run-task-0");

        Assert.True(jobs.IsQueuedOrRunning(id),
            "a freshly queued job reported as finished — the pump would delete its row before it ran");

        release.Release();
        await jobs.StopAsync(CancellationToken.None);
    }
}
