using System.Collections.Concurrent;
using System.Text.Json;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The real orchestrator with only its PowerShell calls faked. Tasks are batch items; each records itself, can
/// hold or wait on a gate, and returns <c>{"tenant": TenantFilter}</c> unless <see cref="Body"/> says otherwise.
/// Tracks the start order and the peak number of tasks running at once.
/// </summary>
internal sealed class FakeOrchestrator(JobManager jobs, WorkStore store, ResultStore results, IConfiguration config,
    CraftSettings settings)
    : OrchestratorService(NullLogger<OrchestratorService>.Instance, null!, null!, jobs, store, results, config, settings)
{
    public const string PostExecScript = "Invoke-CraftPostExecution";

    public readonly ConcurrentQueue<Dictionary<string, object>> Tasks = new();
    public readonly ConcurrentQueue<string> Started = new();
    public readonly ConcurrentQueue<(Dictionary<string, object> Parameters, string[] Lines)> PostExecs = new();

    /// <summary>The task's output; throw to fail it.</summary>
    public Func<Dictionary<string, object>, string>? Body;

    /// <summary>Awaited before the task does anything else (a gate, or a hold for one task).</summary>
    public Func<Dictionary<string, object>, Task>? BeforeRun;

    public Func<Task>? PostExecBody;
    public int HoldMs;
    public int Checkouts, Reclaims;

    private int _active, _maxActive;
    public int MaxActive => Volatile.Read(ref _maxActive);

    public static string IdOf(Dictionary<string, object> task) => task["TenantFilter"].ToString()!;

    internal override string? FindScript(string name) => name;

    internal override async Task<string> RunScriptAsync(string path, Dictionary<string, object> parameters, bool captureOutput,
        PowerShellWorker? worker = null)
    {
        if (path == PostExecScript)
        {
            PostExecs.Enqueue((parameters, File.ReadAllLines((string)parameters["ResultsPath"])));
            if (PostExecBody != null) await PostExecBody();
            return string.Empty;
        }

        var task = JsonSerializer.Deserialize<Dictionary<string, object>>((string)parameters["TaskJson"])!;
        Tasks.Enqueue(task);
        Started.Enqueue(IdOf(task));
        var now = Interlocked.Increment(ref _active);
        for (var seen = _maxActive; now > seen; seen = _maxActive)
            if (Interlocked.CompareExchange(ref _maxActive, now, seen) == seen) break;
        try
        {
            if (BeforeRun != null) await BeforeRun(task);
            if (HoldMs > 0) await Task.Delay(HoldMs);
            var output = Body?.Invoke(task) ?? JsonSerializer.Serialize(new { tenant = IdOf(task) });
            return captureOutput ? output : string.Empty;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    internal override PowerShellWorker? CheckoutSequentialWorker(CancellationToken ct) { Interlocked.Increment(ref Checkouts); return null; }
    internal override void ReclaimSequentialWorker(PowerShellWorker? worker, bool faulted) => Interlocked.Increment(ref Reclaims);
}

/// <summary>Store, pump and JobManager wired as in the host, over an in-memory table store, driven by hand.</summary>
internal sealed class OrchestrationHarness : IAsyncDisposable
{
    public required FakeOrchestrator Svc { get; init; }
    public required WorkStore Store { get; init; }
    public required WorkPump Pump { get; init; }
    public required JobManager Jobs { get; init; }
    public required ICraftTableStore Tables { get; init; }

    public static async Task<OrchestrationHarness> CreateAsync(int poolSize = 4, ICraftTableStore? tables = null,
        Action<CraftSettings>? configure = null)
    {
        var settings = new CraftSettings();
        settings.Worker.BgPoolSize = poolSize;
        configure?.Invoke(settings);
        // The limiter's starting concurrency otherwise follows the CPU count, which differs between machines.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundBaseConcurrency"] = poolSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        var pool = new PowerShellWorkerPool(repo, NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        tables ??= new MemoryTableStore();
        var store = new WorkStore(NullLogger<WorkStore>.Instance, settings, tables);
        var results = new ResultStore(NullLogger<ResultStore>.Instance, settings, tables);
        var svc = new FakeOrchestrator(jobs, store, results, config, settings);
        await svc.ResumeInterruptedRunsAsync(CancellationToken.None);
        var pump = new WorkPump(NullLogger<WorkPump>.Instance, store, jobs, config, settings, svc);
        _ = Task.Run(() => jobs.StartAsync(CancellationToken.None));
        return new OrchestrationHarness { Svc = svc, Store = store, Pump = pump, Jobs = jobs, Tables = tables };
    }

    public static string Batch(int n, string prefix = "t") =>
        JsonSerializer.Serialize(Enumerable.Range(0, n).Select(i => new { Name = "Job", TenantFilter = $"{prefix}{i}", N = i }));

    public Task<bool> Start(string name, string batch, string? postExec = null, string? postParams = null,
        bool sequential = false, int priority = 4, bool allowCollision = true, int maxConcurrency = 0, bool stopOnFailure = false) =>
        Svc.StartFromBatchAsync(name, batch, priority, postExec, postParams, CancellationToken.None, sequential: sequential,
            allowCollision: allowCollision, maxConcurrency: maxConcurrency, stopOnFailure: stopOnFailure);

    public async Task<bool> DriveUntil(Func<Task<bool>> done, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            await Pump.RefillAsync(CancellationToken.None);
            if (await done()) return true;
            await Task.Delay(10);
        }
        return await done();
    }

    public Task<bool> DriveUntilFinished(string name, int timeoutMs = 10_000) =>
        DriveUntil(async () => await Store.GetRunByNameAsync(name) is { IsFinished: true }, timeoutMs);

    public Task<bool> DriveUntilAllFinished(int timeoutMs = 10_000) =>
        DriveUntil(async () => (await ReadyNamesAsync()).Count == 0, timeoutMs);

    public async Task<List<string>> ReadyNamesAsync()
    {
        var names = new List<string>();
        await foreach (var e in Store.ReadReadyAsync()) names.Add(e.Name);
        return names;
    }

    public async ValueTask DisposeAsync() => await Jobs.StopAsync(CancellationToken.None);
}
