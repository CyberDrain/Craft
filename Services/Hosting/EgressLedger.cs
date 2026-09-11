using System.Globalization;
using System.Text.Json;
using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Instance-wide daily egress counter for app-only API clients, persisted to a small local file so a
/// restart does not reset the day's total. One number for the whole instance (deliberately NOT per
/// client): <see cref="ApiEgressLimiterMiddleware"/> records the outbound bytes of each API-client
/// response into it, and asks it whether the instance is over budget before letting the next API
/// request run.
/// <para>
/// The request path only ever touches memory under a short lock — no per-request IO. A background timer
/// flushes the counter to disk every <c>FlushSeconds</c> (and once more on shutdown), and only when it
/// has changed, so an idle instance writes nothing. The file is loaded in the constructor (before the
/// host serves traffic); if it is stamped with today's UTC date the total is restored, otherwise the
/// day starts fresh. Nothing is read back from Azure — see <see cref="EgressLimitSettings"/>.
/// </para>
/// <para>
/// The file lives in the same directory as the log files (<see cref="FileLoggingSettings.ResolvedDirectory"/>,
/// e.g. <c>{home}/logs</c>), which is the writable area confirmed to survive restarts and crashes and is
/// lost only on a slice/image replacement — when a fresh day is the correct behaviour anyway. Storing it
/// there also means it follows any <c>App__FileLogging__Directory</c> override the deployment sets.
/// </para>
/// </summary>
public sealed class EgressLedger : BackgroundService
{
    private readonly ILogger<EgressLedger> _logger;
    private readonly long _capBytes;
    private readonly int _flushSeconds;
    private readonly string _filePath;
    private readonly Func<DateTime> _utcNow;

    private readonly object _lock = new();
    private DateOnly _dateUtc;
    private long _bytes;
    private bool _dirty;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Production constructor — resolves cap/flush from settings and stores the ledger file in
    /// the same directory as the log files, which is the writable area confirmed to survive restarts and
    /// crashes (and honours an App__FileLogging__Directory override).</summary>
    public EgressLedger(ILogger<EgressLedger> logger, CraftSettings settings)
        : this(logger,
               (settings ?? throw new ArgumentNullException(nameof(settings))).RateLimit.Egress.ResolvedBytesPerDay,
               settings.RateLimit.Egress.ResolvedFlushSeconds,
               Path.Combine(settings.FileLogging.ResolvedDirectory, "egress-ledger.json"))
    {
    }

    /// <summary>Test/explicit constructor. <paramref name="utcNow"/> defaults to the real clock.</summary>
    internal EgressLedger(ILogger<EgressLedger> logger, long capBytes, int flushSeconds, string filePath,
        Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _capBytes = Math.Max(0, capBytes);
        _flushSeconds = Math.Max(1, flushSeconds);
        _filePath = filePath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _dateUtc = DateOnly.FromDateTime(_utcNow());
        Load();
    }

    /// <summary>Daily budget in bytes; 0 = accounting only (never rejects).</summary>
    public long CapBytes => _capBytes;

    /// <summary>
    /// True when a budget is set and the instance has already served at least that many bytes today.
    /// Rolls the counter over to a new UTC day first, so this self-resets at midnight with no scheduler.
    /// </summary>
    public bool ShouldReject()
    {
        if (_capBytes <= 0) return false;
        lock (_lock)
        {
            RolloverIfNeeded();
            return _bytes >= _capBytes;
        }
    }

    /// <summary>Add the outbound bytes of one response to today's running total.</summary>
    public void Record(long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            RolloverIfNeeded();
            _bytes += bytes;
            _dirty = true;
        }
    }

    /// <summary>Bytes served so far today (for logging / a future metrics surface).</summary>
    public long CurrentBytes
    {
        get { lock (_lock) { RolloverIfNeeded(); return _bytes; } }
    }

    /// <summary>Seconds from now until the next UTC midnight, floored at 1 — the <c>Retry-After</c> a
    /// rejected caller is handed so it comes back once the daily window has reset.</summary>
    public int SecondsToNextUtcMidnight()
    {
        var now = _utcNow();
        return Math.Max(1, (int)Math.Ceiling((now.Date.AddDays(1) - now).TotalSeconds));
    }

    /// <summary>ISO-8601 timestamp of the next UTC midnight, for the rejection body.</summary>
    public string NextUtcMidnightIso() =>
        _utcNow().Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    // Must be called under _lock.
    private void RolloverIfNeeded()
    {
        var today = DateOnly.FromDateTime(_utcNow());
        if (today == _dateUtc) return;
        _dateUtc = today;
        _bytes = 0;
        _dirty = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[Egress] API egress accounting active — cap {Cap}, flush every {Flush}s, file {File}",
            _capBytes > 0 ? $"{_capBytes} bytes/day" : "accounting only (no cap)",
            _flushSeconds, _filePath);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_flushSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                Flush();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down (the timer await was cancelled) — expected; fall through to the final flush.
        }

        Flush(); // final flush so the last window of counts survives a graceful restart
    }

    /// <summary>Load the persisted counter. Restores only when the file is stamped with today's UTC
    /// date; a stale or unreadable file leaves today starting from zero.</summary>
    internal void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var state = JsonSerializer.Deserialize<LedgerState>(File.ReadAllText(_filePath), s_json);
            if (state is null) return;

            var today = DateOnly.FromDateTime(_utcNow());
            var sameDay = DateOnly.TryParseExact(state.DateUtc, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var stored) && stored == today;

            if (sameDay && state.Bytes >= 0)
            {
                lock (_lock)
                {
                    _dateUtc = today;
                    _bytes = state.Bytes;
                    _dirty = false;
                }
                _logger.LogInformation("[Egress] Restored {Bytes} bytes already served today from {File}",
                    state.Bytes, _filePath);
            }
            else
            {
                _logger.LogInformation("[Egress] Ledger file not for today ({Stored}) — starting the day fresh",
                    state.DateUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Failed to load ledger — starting fresh");
        }
    }

    /// <summary>Rewrite the counter to disk via a temp file + atomic rename, but only when it has
    /// changed since the last flush. Failures are logged and retried on the next tick.</summary>
    internal void Flush()
    {
        try
        {
            LedgerState snapshot;
            lock (_lock)
            {
                if (!_dirty) return;
                RolloverIfNeeded();
                snapshot = new LedgerState
                {
                    DateUtc = _dateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Bytes = _bytes,
                };
                _dirty = false;
            }

            var dir = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, s_json));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Failed to flush ledger to disk");
            lock (_lock) { _dirty = true; } // don't lose the change — try again next tick
        }
    }

    private sealed class LedgerState
    {
        public string DateUtc { get; set; } = "";
        public long Bytes { get; set; }
    }
}
