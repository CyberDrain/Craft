namespace Craft.Services;

/// <summary>
/// Gates all background (non-HTTP) work behind a dynamic concurrency semaphore
/// that responds to both BG queue pressure and HTTP pool pressure.
///
/// Design:
///   - Starts at a configurable baseline concurrency (default: ProcessorCount clamped 2-4).
///   - A monitor timer checks every 30s and adjusts concurrency based on two signals:
///
///   BG scale-up: if BG tasks have been queued (waiting) for a sustained period,
///     doubles concurrency up to the ceiling.
///   BG scale-down: when queue drains to 0 active + 0 waiting, scales back to baseline.
///
///   HTTP pressure throttle: if the number of busy HTTP workers meets or exceeds
///     HttpPressureThreshold for a sustained period, BG concurrency is reduced to 1
///     to give HTTP maximum CPU headroom. When HTTP pressure drops, BG concurrency
///     restores to baseline.
///
///   - Ceiling is capped to BgPoolSize (the real bottleneck).
///   - HTTP paths (ExecuteHttpScript) must NOT go through this limiter.
/// </summary>
public class BackgroundTaskLimiter : IDisposable
{
    private readonly ILogger<BackgroundTaskLimiter> _logger;
    private readonly PowerShellWorkerPool _pool;
    private readonly object _scaleLock = new();
    private readonly Timer _monitorTimer;

    private SemaphoreSlim _semaphore;
    private int _currentMax;
    private int _active;
    private int _waiting;
    private DateTime? _queuePressureSince;
    private DateTime? _httpPressureSince;
    private bool _httpThrottled;

    public int BaseConcurrency { get; }
    public int CeilingConcurrency { get; }
    public TimeSpan ScaleUpAfter { get; }

    /// <summary>
    /// Number of busy HTTP workers that triggers BG throttling.
    /// When HttpPoolSize - HttpAvailable >= this value for HttpPressureSeconds,
    /// BG concurrency drops to 1.
    /// Default: half of HttpPoolSize (e.g. 2 on a 4-worker pool).
    /// Set to 0 to disable HTTP pressure throttling.
    /// </summary>
    public int HttpPressureThreshold { get; }

    /// <summary>
    /// How long HTTP pressure must be sustained before throttling BG tasks.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan HttpPressureAfter { get; }

    public int CurrentMax => _currentMax;
    public int Active => _active;
    public int Waiting => _waiting;
    public bool IsHttpThrottled => _httpThrottled;

    public BackgroundTaskLimiter(ILogger<BackgroundTaskLimiter> logger, IConfiguration configuration,
        CraftSettings settings, PowerShellWorkerPool pool)
    {
        _logger = logger;
        _pool = pool;

        // Baseline: low memory footprint when idle
        BaseConcurrency = configuration.GetValue("BackgroundBaseConcurrency",
            Math.Clamp(Environment.ProcessorCount, 2, 4));

        // Ceiling: capped to BgPoolSize since the pool is the real bottleneck.
        var bgPoolSize = Math.Max(1, settings.Worker.BgPoolSize);
        CeilingConcurrency = configuration.GetValue("BackgroundMaxConcurrency", bgPoolSize);

        // How long the BG queue must be backed up before we scale up
        ScaleUpAfter = TimeSpan.FromSeconds(
            configuration.GetValue("BackgroundScaleUpAfterSeconds", 15));

        // HTTP pressure: when this many HTTP workers are busy, throttle BG to 1
        var httpPoolSize = Math.Max(1, settings.Worker.HttpPoolSize);
        HttpPressureThreshold = configuration.GetValue("BackgroundHttpPressureThreshold",
            Math.Max(1, httpPoolSize / 2));

        // How long HTTP pressure must persist before throttling
        HttpPressureAfter = TimeSpan.FromSeconds(
            configuration.GetValue("BackgroundHttpPressureAfterSeconds", 10));

        _currentMax = BaseConcurrency;
        _semaphore = new SemaphoreSlim(BaseConcurrency, CeilingConcurrency);

        // Monitor timer: checks queue and HTTP pressure every 10s
        _monitorTimer = new Timer(MonitorCallback, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _logger.LogInformation("[System] Limiter init: baseline={Base} ceiling={Ceiling} scaleAfter={ScaleAfter}s " +
            "httpPressureThreshold={HttpThreshold} httpPressureAfter={HttpAfter}s cpus={Cpus}",
            BaseConcurrency, CeilingConcurrency, ScaleUpAfter.TotalSeconds,
            HttpPressureThreshold, HttpPressureAfter.TotalSeconds, Environment.ProcessorCount);
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
            _logger.LogDebug("[System] Limiter: {Active} active {Waiting} waiting max={Max} httpThrottled={Throttled} heap={HeapMB}MB",
                active, waiting, _currentMax, _httpThrottled, GC.GetTotalMemory(false) / (1024 * 1024));
        }

        // ── HTTP pressure check ────────────────────────────────────────
        CheckHttpPressure();

        // ── BG queue pressure (only when not HTTP-throttled) ───────────
        if (!_httpThrottled)
        {
            if (waiting > 0)
            {
                _queuePressureSince ??= DateTime.UtcNow;
                var pressureDuration = DateTime.UtcNow - _queuePressureSince.Value;
                if (pressureDuration >= ScaleUpAfter && _currentMax < CeilingConcurrency)
                    ScaleUp();
            }
            else if (active == 0 && _currentMax > BaseConcurrency)
            {
                ScaleDown(BaseConcurrency, "idle");
            }
            else
            {
                _queuePressureSince = null;
            }
        }
    }

    private void CheckHttpPressure()
    {
        if (HttpPressureThreshold <= 0) return; // disabled

        var httpBusy = _pool.HttpPoolSize - _pool.HttpAvailable;
        var underPressure = httpBusy >= HttpPressureThreshold;

        if (underPressure && !_httpThrottled)
        {
            _httpPressureSince ??= DateTime.UtcNow;
            var duration = DateTime.UtcNow - _httpPressureSince.Value;
            if (duration >= HttpPressureAfter)
            {
                // Sustained HTTP pressure — throttle BG to minimum (2)
                _logger.LogInformation("[System] Limiter: HTTP pressure detected ({Busy}/{Total} workers busy for {Sec}s), " +
                    "throttling BG to 2", httpBusy, _pool.HttpPoolSize, duration.TotalSeconds);
                _httpThrottled = true;
                _queuePressureSince = null; // reset BG scale-up tracking
                ScaleDown(2, "HTTP pressure");
            }
        }
        else if (!underPressure && _httpThrottled)
        {
            // Pressure relieved — restore to baseline
            _logger.LogInformation("[System] Limiter: HTTP pressure relieved, restoring BG to baseline={Base}", BaseConcurrency);
            _httpThrottled = false;
            _httpPressureSince = null;
            RestoreToBaseline();
        }
        else if (!underPressure)
        {
            _httpPressureSince = null;
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

    private void ScaleDown(int target, string reason)
    {
        lock (_scaleLock)
        {
            if (_currentMax <= target) return;

            var oldMax = _currentMax;
            var slotsToRemove = _currentMax - target;

            // Absorb excess slots by waiting on the semaphore without releasing.
            // Non-blocking when active==0; best-effort when tasks are running.
            for (var i = 0; i < slotsToRemove; i++)
            {
                if (!_semaphore.Wait(0)) break;
            }

            _currentMax = target;
            _queuePressureSince = null;

            _logger.LogInformation("[System] Limiter scaled DOWN: {OldMax} -> {NewMax} ({Reason})",
                oldMax, _currentMax, reason);
        }
    }

    private void RestoreToBaseline()
    {
        lock (_scaleLock)
        {
            if (_currentMax >= BaseConcurrency) return;

            var slotsToAdd = BaseConcurrency - _currentMax;
            _semaphore.Release(slotsToAdd);
            _currentMax = BaseConcurrency;

            _logger.LogInformation("[System] Limiter restored to baseline: {Max}", _currentMax);
        }
    }

    public void Dispose()
    {
        _monitorTimer.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
