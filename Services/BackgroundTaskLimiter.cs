namespace CRAFT.Services;

/// <summary>
/// Gates all background (non-HTTP) work behind a dynamic concurrency semaphore
/// and drops thread priority to BelowNormal so the OS scheduler always prefers
/// HTTP request threads when the CPU is contended.
///
/// Design:
///   - Starts at a low baseline concurrency (default 4).
///   - A monitor timer checks every 30s: if tasks have been queued (waiting)
///     for a sustained period, it doubles the concurrency up to a ceiling.
///   - When the queue drains to 0 active + 0 waiting, it scales back to baseline.
///   - The RunspacePool max is set to the ceiling so runspaces are created
///     lazily — memory only grows when actually needed.
///   - Thread priority is lowered so HTTP stays responsive at any scale.
///   - HTTP paths (ExecuteHttpScript) must NOT go through this limiter.
/// </summary>
public class BackgroundTaskLimiter : IDisposable
{
    private readonly ILogger<BackgroundTaskLimiter> _logger;
    private readonly object _scaleLock = new();
    private readonly Timer _monitorTimer;

    private SemaphoreSlim _semaphore;
    private int _currentMax;
    private int _active;
    private int _waiting;
    private DateTime? _queuePressureSince;

    public int BaseConcurrency { get; }
    public int CeilingConcurrency { get; }
    public TimeSpan ScaleUpAfter { get; }
    public int CurrentMax => _currentMax;
    public int Active => _active;
    public int Waiting => _waiting;

    public BackgroundTaskLimiter(ILogger<BackgroundTaskLimiter> logger, IConfiguration configuration)
    {
        _logger = logger;

        // Baseline: low memory footprint when idle
        BaseConcurrency = configuration.GetValue("BackgroundBaseConcurrency",
            Math.Clamp(Environment.ProcessorCount, 2, 4));

        // Ceiling: capped to BgPoolSize since the pool is the real bottleneck.
        // Allowing the limiter to exceed pool size just wastes semaphore slots
        // on tasks blocked waiting for a worker.
        var bgPoolSize = Math.Max(1, configuration.GetValue("PowerShell:BgPoolSize", 4));
        CeilingConcurrency = configuration.GetValue("BackgroundMaxConcurrency", bgPoolSize);

        // How long the queue must be backed up before we scale up
        ScaleUpAfter = TimeSpan.FromSeconds(
            configuration.GetValue("BackgroundScaleUpAfterSeconds", 15));

        _currentMax = BaseConcurrency;
        _semaphore = new SemaphoreSlim(BaseConcurrency, CeilingConcurrency);

        // Monitor timer: checks queue pressure every 30s
        _monitorTimer = new Timer(MonitorCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        _logger.LogInformation("[System] Limiter init: baseline={Base} ceiling={Ceiling} scaleAfter={ScaleAfter}s cpus={Cpus}",
            BaseConcurrency, CeilingConcurrency, ScaleUpAfter.TotalSeconds, Environment.ProcessorCount);
    }

    /// <summary>
    /// Run a background task with concurrency limiting.
    /// With BeginInvoke-based execution, work() is truly async (no blocked threads),
    /// so Task.Run and thread priority manipulation are no longer needed.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, string taskName, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _waiting);
        _logger.LogDebug("Background task queued: {Task} ({Active} active, {Waiting} waiting, {Max} max)",
            taskName, _active, _waiting, _currentMax);

        var queueSw = System.Diagnostics.Stopwatch.StartNew();
        await _semaphore.WaitAsync(ct);
        queueSw.Stop();
        Interlocked.Decrement(ref _waiting);
        Interlocked.Increment(ref _active);
        try
        {
            if (queueSw.ElapsedMilliseconds > 500)
            {
                _logger.LogInformation("Background task started after {QueueMs}ms wait: {Task} ({Active} active, {Waiting} waiting)",
                    queueSw.ElapsedMilliseconds, taskName, _active, _waiting);
            }
            else
            {
                _logger.LogDebug("Background task starting: {Task} ({Active} active, {Waiting} waiting, {Max} max, waited {QueueMs}ms)",
                    taskName, _active, _waiting, _currentMax, queueSw.ElapsedMilliseconds);
            }

            return await work();
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            _semaphore.Release();
            _logger.LogDebug("Background task completed: {Task} ({Active} active, {Waiting} waiting, {Max} max)",
                taskName, _active, _waiting, _currentMax);
        }
    }

    /// <summary>
    /// Fire-and-forget overload for void background work.
    /// </summary>
    public async Task RunAsync(Func<Task> work, string taskName, CancellationToken ct = default)
    {
        await RunAsync(async () => { await work(); return 0; }, taskName, ct);
    }

    private void MonitorCallback(object? state)
    {
        var waiting = _waiting;
        var active = _active;

        if (active > 0 || waiting > 0)
        {
            _logger.LogDebug("[System] Limiter: {Active} active {Waiting} waiting max={Max} heap={HeapMB}MB",
                active, waiting, _currentMax, GC.GetTotalMemory(false) / (1024 * 1024));
        }

        if (waiting > 0)
        {
            // Tasks are queued — track how long
            _queuePressureSince ??= DateTime.UtcNow;

            var pressureDuration = DateTime.UtcNow - _queuePressureSince.Value;
            if (pressureDuration >= ScaleUpAfter && _currentMax < CeilingConcurrency)
            {
                ScaleUp();
            }
        }
        else if (active == 0 && _currentMax > BaseConcurrency)
        {
            // Queue empty + nothing running — scale back down
            ScaleDown();
        }
        else
        {
            // Tasks running but nothing waiting — pressure relieved
            _queuePressureSince = null;
        }
    }

    private void ScaleUp()
    {
        lock (_scaleLock)
        {
            if (_currentMax >= CeilingConcurrency) return;

            var newMax = Math.Min(_currentMax * 2, CeilingConcurrency);
            var slotsToAdd = newMax - _currentMax;

            // Release extra slots into the semaphore to increase capacity
            _semaphore.Release(slotsToAdd);
            _currentMax = newMax;
            _queuePressureSince = null; // reset timer for next doubling

            _logger.LogInformation("[System] Limiter scaled UP: {OldMax} -> {NewMax} ({Waiting} waiting)",
                _currentMax / 2, _currentMax, _waiting);
        }
    }

    private void ScaleDown()
    {
        lock (_scaleLock)
        {
            if (_currentMax <= BaseConcurrency) return;

            var oldMax = _currentMax;
            var slotsToRemove = _currentMax - BaseConcurrency;

            // Absorb excess slots by waiting on the semaphore without releasing
            // This is non-blocking because active==0 means all slots are available
            for (var i = 0; i < slotsToRemove; i++)
            {
                if (!_semaphore.Wait(0)) break; // don't block if slots are in use
            }

            _currentMax = BaseConcurrency;
            _queuePressureSince = null;

            _logger.LogInformation("[System] Limiter scaled DOWN: {OldMax} -> {NewMax} (idle)",
                oldMax, _currentMax);
        }
    }

    public void Dispose()
    {
        _monitorTimer.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
