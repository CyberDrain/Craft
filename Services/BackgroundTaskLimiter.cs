namespace Craft.Services;

/// <summary>
/// Gates all background (non-HTTP) work behind a dynamic concurrency gate
/// that responds to both BG queue pressure and HTTP pool pressure.
///
/// Design:
///   - Starts at a configurable baseline concurrency (default: ProcessorCount clamped 2-4).
///   - A monitor timer checks every 10s and adjusts concurrency based on two signals:
///
///   BG scale-up: if BG tasks have been queued (waiting) for a sustained period,
///     doubles concurrency up to the ceiling.
///   BG scale-down: when queue drains to 0 active + 0 waiting, scales back to baseline.
///
///   HTTP pressure throttle: if the number of busy HTTP workers meets or exceeds
///     HttpPressureThreshold for a sustained period, BG concurrency is reduced to 2
///     to give HTTP maximum CPU headroom. When HTTP pressure drops, BG concurrency
///     restores to baseline.
///
///   - Ceiling is capped to BgPoolSize (the real bottleneck).
///   - HTTP paths (ExecuteHttpScript) must NOT go through this limiter.
///
/// Implementation:
///   Uses a counter-based gate (_active vs _currentMax) with a FIFO waiter queue
///   instead of SemaphoreSlim. This eliminates permit-leak bugs that occur when
///   scaling down while tasks are running — there are no permits to leak, just a
///   threshold comparison under a single lock.
/// </summary>
public class BackgroundTaskLimiter : IDisposable
{
    private readonly ILogger<BackgroundTaskLimiter> _logger;
    private readonly PowerShellWorkerPool _pool;
    private readonly object _gateLock = new();
    private readonly LinkedList<TaskCompletionSource<bool>> _waiters = new();
    private readonly Timer _monitorTimer;

    private int _currentMax;
    private int _active;
    private int _waiting;
    private DateTime? _queuePressureSince;
    private DateTime? _httpPressureSince;
    private bool _httpThrottled;
    private readonly bool _burstToCeiling;
    private readonly int _overSubscribe;

    public int BaseConcurrency { get; }
    public int CeilingConcurrency { get; }
    public TimeSpan ScaleUpAfter { get; }

    /// <summary>When true, the limiter jumps straight to the ceiling the moment tasks start queuing,
    /// instead of waiting <see cref="ScaleUpAfter"/> to ramp — for bursty fan-out. Default false.</summary>
    public bool BurstToCeiling => _burstToCeiling;

    /// <summary>Extra concurrency admitted ON TOP of the worker-target (ceiling), so this many tasks can do
    /// their pre-invoke table writes (the "Running" marker) and queue at the worker checkout while the pool
    /// stays full — closing the slot-held-during-I/O gap. Real worker concurrency is still capped by the pool.
    /// Default 0.</summary>
    public int OverSubscribe => _overSubscribe;

    /// <summary>Admission limit = the ramp-controlled worker target plus the over-subscribe headroom.</summary>
    public int EffectiveMax => _currentMax + _overSubscribe;

    /// <summary>
    /// Number of busy HTTP workers that triggers BG throttling.
    /// When HttpPoolSize - HttpAvailable >= this value for HttpPressureSeconds,
    /// BG concurrency drops to 2.
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

        // Burst: jump straight to ceiling on the first sign of queueing (skip the ScaleUpAfter dwell).
        _burstToCeiling = configuration.GetValue("BackgroundBurstToCeiling", false);

        // Over-subscription: admit this many tasks ABOVE the worker target so they can do their pre-invoke
        // table writes and queue at the worker checkout while the pool stays full. 0 = off (strict pool cap).
        _overSubscribe = Math.Max(0, configuration.GetValue("BackgroundOverSubscribe", 0));

        _currentMax = BaseConcurrency;

        // Monitor timer: checks queue and HTTP pressure every 10s
        _monitorTimer = new Timer(MonitorCallback, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _logger.LogInformation("[System] Limiter init: baseline={Base} ceiling={Ceiling} scaleAfter={ScaleAfter}s " +
            "burstToCeiling={Burst} overSubscribe={Over} httpPressureThreshold={HttpThreshold} httpPressureAfter={HttpAfter}s cpus={Cpus}",
            BaseConcurrency, CeilingConcurrency, ScaleUpAfter.TotalSeconds, _burstToCeiling, _overSubscribe,
            HttpPressureThreshold, HttpPressureAfter.TotalSeconds, Environment.ProcessorCount);
    }

    /// <summary>
    /// Run a background task with concurrency limiting.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, string taskName, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await AcquireAsync(taskName, ct);
        sw.Stop();

        try
        {
            if (sw.ElapsedMilliseconds > 500)
            {
                _logger.LogInformation("Background task started after {QueueMs}ms wait: {Task} ({Active} active, {Waiting} waiting)",
                    sw.ElapsedMilliseconds, taskName, _active, _waiting);
            }
            else
            {
                _logger.LogDebug("Background task starting: {Task} ({Active} active, {Waiting} waiting, {Max} max, waited {QueueMs}ms)",
                    taskName, _active, _waiting, _currentMax, sw.ElapsedMilliseconds);
            }

            return await work();
        }
        finally
        {
            ReleaseSlot();
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

    /// <summary>
    /// Acquire a concurrency slot from the limiter. The caller MUST call <see cref="ReleaseSlot"/>
    /// when the work is done. This is the split version of <see cref="RunAsync{T}"/> for use by
    /// JobManager, which needs to own the work lifecycle separately from slot acquisition.
    /// </summary>
    public async Task AcquireAsync(string taskName, CancellationToken ct = default)
    {
        LinkedListNode<TaskCompletionSource<bool>>? node = null;

        lock (_gateLock)
        {
            // Burst-to-ceiling: the moment demand reaches the worker target, jump straight to the ceiling
            // instead of waiting ScaleUpAfter to ramp. (HTTP pressure still wins — don't fight the throttle.)
            if (_burstToCeiling && _active >= _currentMax && _currentMax < CeilingConcurrency && !_httpThrottled)
            {
                var old = _currentMax;
                _currentMax = CeilingConcurrency;
                _queuePressureSince = null;
                _logger.LogInformation("[System] Limiter burst-to-ceiling: {Old} -> {New}", old, _currentMax);
            }

            if (_active < _currentMax + _overSubscribe)
            {
                // Slot available — grant immediately
                _active++;
                _logger.LogDebug("Limiter slot granted immediately: {Task} ({Active} active, {Waiting} waiting, {Max} max)",
                    taskName, _active, _waiting, _currentMax);
                return;
            }

            // No slot available — enqueue a FIFO waiter
            _waiting++;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(tcs);
        }

        _logger.LogDebug("Limiter slot requested: {Task} ({Active} active, {Waiting} waiting, {Max} max)",
            taskName, _active, _waiting, _currentMax);

        // Register cancellation to remove from queue without granting
        using var ctr = ct.Register(() =>
        {
            lock (_gateLock)
            {
                // Only cancel if still in the queue (not yet granted)
                if (node.List != null)
                {
                    _waiters.Remove(node);
                    _waiting--;
                    node.Value.TrySetCanceled(ct);
                }
            }
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await node.Value.Task; // completes when granted or throws if cancelled
        sw.Stop();

        // Slot was granted — _active incremented and _waiting decremented by granter
        if (sw.ElapsedMilliseconds > 500)
        {
            _logger.LogInformation("Limiter slot acquired after {WaitMs}ms: {Task} ({Active} active, {Waiting} waiting)",
                sw.ElapsedMilliseconds, taskName, _active, _waiting);
        }
    }

    /// <summary>
    /// Release a concurrency slot previously acquired via <see cref="AcquireAsync"/>.
    /// </summary>
    public void ReleaseSlot()
    {
        lock (_gateLock)
        {
            _active--;
            GrantWaiters();
        }
    }

    /// <summary>
    /// Grant slots to queued waiters while capacity allows. Must be called under <see cref="_gateLock"/>.
    /// </summary>
    private void GrantWaiters()
    {
        while (_active < _currentMax + _overSubscribe && _waiters.Count > 0)
        {
            var node = _waiters.First!;
            _waiters.RemoveFirst();

            if (node.Value.TrySetResult(true))
            {
                // Successfully granted — transfer from waiting to active
                _active++;
                _waiting--;
            }
            // If TrySetResult failed, the waiter was cancelled — its cancellation
            // handler already decremented _waiting and removed it from the list.
            // The node is stale; move to next.
        }
    }

    private void MonitorCallback(object? state)
    {
        var waiting = _waiting;
        var active = _active;

        if (active > 0 || waiting > 0)
        {
            var memSnapshot = GetMemorySnapshot();
            _logger.LogDebug("[System] Limiter: {Active} active {Waiting} waiting max={Max} httpThrottled={Throttled} {Memory}",
                active, waiting, _currentMax, _httpThrottled, memSnapshot);
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
            ScaleUp(BaseConcurrency);
        }
        else if (!underPressure)
        {
            _httpPressureSince = null;
        }
    }

    /// <summary>
    /// Double concurrency up to the ceiling. Wakes queued waiters if capacity opens.
    /// </summary>
    private void ScaleUp()
    {
        lock (_gateLock)
        {
            if (_currentMax >= CeilingConcurrency) return;

            var oldMax = _currentMax;
            _currentMax = Math.Min(_currentMax * 2, CeilingConcurrency);
            _queuePressureSince = null;

            _logger.LogInformation("[System] Limiter scaled UP: {OldMax} -> {NewMax} ({Waiting} waiting)",
                oldMax, _currentMax, _waiting);

            GrantWaiters();
        }
    }

    /// <summary>
    /// Set concurrency to a specific target (for restoring after HTTP pressure).
    /// Wakes queued waiters if the new target is higher than the current max.
    /// </summary>
    private void ScaleUp(int target)
    {
        lock (_gateLock)
        {
            if (_currentMax >= target) return;

            var oldMax = _currentMax;
            _currentMax = Math.Min(target, CeilingConcurrency);

            _logger.LogInformation("[System] Limiter restored: {OldMax} -> {NewMax}", oldMax, _currentMax);

            GrantWaiters();
        }
    }

    /// <summary>
    /// Reduce the concurrency limit. Existing tasks complete naturally — no forceful
    /// eviction. New tasks simply won't be admitted until _active drops below _currentMax.
    /// This is inherently safe: there are no semaphore permits to leak.
    /// </summary>
    private void ScaleDown(int target, string reason)
    {
        lock (_gateLock)
        {
            if (_currentMax <= target) return;

            var oldMax = _currentMax;
            _currentMax = target;
            _queuePressureSince = null;

            _logger.LogInformation("[System] Limiter scaled DOWN: {OldMax} -> {NewMax} ({Reason}, {Active} active will drain naturally)",
                oldMax, _currentMax, reason, _active);
        }
    }

    /// <summary>
    /// Build a compact memory snapshot string for log output.
    /// Shows GC heap, process working set (RSS), committed bytes, and GC generation counts.
    /// The working set is what the container cgroup tracks — heap alone can be misleading
    /// because unmanaged allocations (HTTP/TLS buffers, PS engine state) are invisible to GC.
    /// </summary>
    internal static string GetMemorySnapshot()
    {
        var heapBytes = GC.GetTotalMemory(false);
        var workingSet = Environment.WorkingSet;
        var gcInfo = GC.GetGCMemoryInfo();
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        return $"heap={heapBytes / (1024 * 1024)}MB rss={workingSet / (1024 * 1024)}MB " +
               $"committed={gcInfo.TotalCommittedBytes / (1024 * 1024)}MB " +
               $"gc=[{gen0}/{gen1}/{gen2}]";
    }

    public void Dispose()
    {
        _monitorTimer.Dispose();
        // Cancel all queued waiters
        lock (_gateLock)
        {
            while (_waiters.Count > 0)
            {
                var node = _waiters.First!;
                _waiters.RemoveFirst();
                _waiting--;
                node.Value.TrySetCanceled();
            }
        }
        GC.SuppressFinalize(this);
    }
}
