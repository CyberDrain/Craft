using System.Collections.Concurrent;
using System.Reflection;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Pins the re-drive watchdog's bounds: the status sweep starts it for every live run each tick without
/// awaiting it, so it must never run two verifications for one run, never more than a few at once across
/// runs, and never re-drive a candidate that finished while its verification was waiting.
/// </summary>
public class OrchestratorRedriveGuardTests
{
    private sealed record Harness(OrchestratorService Svc, JobQueueStore Queue,
        RunRemainingCounterTests.ConditionalStore Backing, string IndexTable);

    private static async Task<Harness> NewHarnessAsync()
    {
        var settings = new CraftSettings { Orchestrator = { TablePrefix = "rdg" + Guid.NewGuid().ToString("N")[..8] } };
        var backing = new RunRemainingCounterTests.ConditionalStore();
        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, backing);
        await queue.InitializeAsync();

        var svc = (OrchestratorService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrchestratorService));
        Set(svc, "_logger", NullLogger<OrchestratorService>.Instance);
        Set(svc, "_queue", queue);
        Set(svc, "_settings", settings);
        Set(svc, "_lock", new object());
        Set(svc, "_activeSequentialDrivers", new ConcurrentDictionary<string, bool>());
        Set(svc, "_requeueFailures", new ConcurrentDictionary<string, int>());
        Set(svc, "_deferrals", NewFieldValue("_deferrals"));
        Set(svc, "_redriveBackoff", NewFieldValue("_redriveBackoff"));
        Set(svc, "_redriveInFlight", NewFieldValue("_redriveInFlight"));
        Set(svc, "_redriveSlots", new SemaphoreSlim(8, 8));
        Set(svc, "_redriveBackoffEnabled", false);
        Set(svc, "_redriveBase", TimeSpan.FromSeconds(60));
        var jm = (JobManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JobManager));
        var jobsField = typeof(JobManager).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jobsField.SetValue(jm, Activator.CreateInstance(jobsField.FieldType));
        Set(svc, "_jobManager", jm);
        return new Harness(svc, queue, backing, $"{settings.Orchestrator.TablePrefix}QueueIndex");
    }

    private static object NewFieldValue(string field) =>
        Activator.CreateInstance(typeof(OrchestratorService)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType)!;

    private static void Set(object target, string field, object? value) =>
        typeof(OrchestratorService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static Task Redrive(Harness h, OrchestratorRun run) =>
        (Task)typeof(OrchestratorService).GetMethod("RedrivePendingTasksAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(h.Svc, [run])!;

    private static OrchestratorRun MakeRun(string name, int count) => new()
    {
        Name = name,
        Status = "Running",
        Priority = 4,
        StartedUtc = DateTime.UtcNow,
        Tasks = Enumerable.Range(0, count)
            .Select(i => new OrchestratorTaskItem { Id = $"{name}_t{i}", Status = "Pending" }).ToList()
    };

    /// <summary>Holds every index read open until <see cref="Release"/>, counting how many are held at once.</summary>
    private sealed class ReadGate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;
        public int Entered;
        public int MaxHeld;

        public ReadGate(Harness h) => h.Backing.OnPartitionQuery = async table =>
        {
            if (table != h.IndexTable) return;
            Interlocked.Increment(ref Entered);
            var held = Interlocked.Increment(ref _held);
            int seen;
            while (held > (seen = Volatile.Read(ref MaxHeld)) && Interlocked.CompareExchange(ref MaxHeld, held, seen) != seen) { }
            await _open.Task;
            Interlocked.Decrement(ref _held);
        };

        public void Release() => _open.TrySetResult();

        public async Task WaitForEnteredAsync(int count)
        {
            for (var i = 0; i < 200 && Volatile.Read(ref Entered) < count; i++) await Task.Delay(10);
        }
    }

    [Fact]
    public async Task SecondTick_WhileAVerificationIsInFlight_DoesNotStartAnother()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("inflight", 3);
        await h.Queue.EnqueueBatchAsync(run.Name, run.Tasks.Select(t => (t.Id, 4)).ToList(), DateTime.UtcNow);
        var gate = new ReadGate(h);

        var first = Redrive(h, run);
        await gate.WaitForEnteredAsync(1);
        var second = Redrive(h, run);

        Assert.True(second.IsCompleted);
        gate.Release();
        await first;
        Assert.Equal(1, gate.Entered);
    }

    [Fact]
    public async Task ManyRuns_VerifyAtMostEightAtOnce_AndAllEventuallyVerify()
    {
        var h = await NewHarnessAsync();
        var runs = Enumerable.Range(0, 20).Select(i => MakeRun($"cap{i:D2}", 2)).ToList();
        foreach (var run in runs)
            await h.Queue.EnqueueBatchAsync(run.Name, run.Tasks.Select(t => (t.Id, 4)).ToList(), DateTime.UtcNow);
        var gate = new ReadGate(h);

        var ticks = runs.Select(r => Redrive(h, r)).ToList();
        await gate.WaitForEnteredAsync(8);
        await Task.Delay(100);

        Assert.Equal(8, gate.Entered);
        gate.Release();
        await Task.WhenAll(ticks);
        Assert.Equal(20, gate.Entered);
        Assert.True(gate.MaxHeld <= 8);
    }

    [Fact]
    public async Task CandidateThatFinishesDuringTheRead_IsNotReDriven()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("finished", 1);
        var gate = new ReadGate(h);

        var tick = Redrive(h, run);
        await gate.WaitForEnteredAsync(1);
        run.Tasks[0].Status = "Completed";
        gate.Release();
        await tick;
        await Task.Delay(200);

        Assert.Empty(await h.Queue.GetQueuedTaskIdsAsync(run.Name));
    }

    [Fact]
    public async Task OrphanStillPendingAfterTheRead_IsReDriven()
    {
        var h = await NewHarnessAsync();
        var run = MakeRun("orphan", 1);

        await Redrive(h, run);
        for (var i = 0; i < 100 && (await h.Queue.GetQueuedTaskIdsAsync(run.Name)).Count == 0; i++) await Task.Delay(20);

        Assert.Equal(["orphan_t0"], await h.Queue.GetQueuedTaskIdsAsync(run.Name));
    }
}
