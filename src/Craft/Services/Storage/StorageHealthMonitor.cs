using System.Diagnostics;

namespace Craft.Storage;

/// <summary>
/// Caches storage-backend reachability for the health endpoint. Pings <see cref="ICraftTableStore"/>
/// with a short timeout and caches the result; <c>/healthz</c> reads the cached value and triggers a
/// non-blocking refresh when it is stale, so the endpoint never blocks on a slow/unreachable backend.
/// </summary>
public sealed class StorageHealthMonitor
{
    private readonly ICraftTableStore _store;
    private readonly ILogger<StorageHealthMonitor> _logger;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(3);

    private volatile bool _healthy;
    private long _lastCheckTimestamp; // Stopwatch ticks; 0 = never checked
    private int _refreshing;

    public StorageHealthMonitor(ICraftTableStore store, ILogger<StorageHealthMonitor> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Last known storage health. If the cached value is older than the TTL (or never taken), kicks off
    /// a non-blocking refresh and returns the current cached value.
    /// </summary>
    public bool Snapshot()
    {
        var last = Interlocked.Read(ref _lastCheckTimestamp);
        var stale = last == 0 || Stopwatch.GetElapsedTime(last) > _ttl;
        if (stale && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            _ = RefreshAsync();
        return _healthy;
    }

    /// <summary>
    /// Polls the backend until it is reachable or <paramref name="timeout"/> elapses, with backoff and
    /// progress logging. Used at startup so the host does not touch storage before it accepts
    /// connections (e.g. a DB that finishes booting a moment after the app). Returns true if storage
    /// became ready within the timeout; false (and a warning) if it timed out.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var delay = TimeSpan.FromSeconds(1);
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            if (await RefreshAsync())
            {
                if (attempt > 1)
                    _logger.LogInformation("[Health] Storage backend ready after {Attempts} attempts ({Elapsed:0.0}s)",
                        attempt, sw.Elapsed.TotalSeconds);
                return true;
            }
            if (sw.Elapsed >= timeout)
            {
                _logger.LogWarning("[Health] Storage backend still unreachable after {Elapsed:0}s — continuing",
                    sw.Elapsed.TotalSeconds);
                return false;
            }
            _logger.LogInformation("[Health] Waiting for storage backend (attempt {Attempt}, {Elapsed:0.0}s)...",
                attempt, sw.Elapsed.TotalSeconds);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return false; }
            delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 1.5));
        }
        return _healthy;
    }

    /// <summary>Ping the storage backend and update the cached health flag. Transitions are logged.</summary>
    public async Task<bool> RefreshAsync()
    {
        try
        {
            var cts = new CancellationTokenSource(_timeout);
            var ping = _store.PingAsync(cts.Token);
            // Bound detection latency by _timeout even if the driver's own cancellation is slow against a
            // frozen server: give up after the timeout and quietly observe the orphaned task's eventual failure.
            if (await Task.WhenAny(ping, Task.Delay(_timeout)) != ping)
            {
                cts.Cancel();
                _ = ping.ContinueWith(t => { _ = t.Exception; cts.Dispose(); }, TaskScheduler.Default);
                throw new TimeoutException($"storage ping did not complete within {_timeout.TotalSeconds:0}s");
            }
            await ping; // observe success/failure
            cts.Dispose();
            if (!_healthy) _logger.LogInformation("[Health] Storage backend reachable");
            _healthy = true;
        }
        catch (Exception ex)
        {
            if (_healthy) _logger.LogWarning("[Health] Storage backend unreachable: {Message}", ex.Message);
            _healthy = false;
        }
        finally
        {
            Interlocked.Exchange(ref _lastCheckTimestamp, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _refreshing, 0);
        }
        return _healthy;
    }
}
