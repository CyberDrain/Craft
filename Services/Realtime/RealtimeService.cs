using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Craft.Configuration;
using Craft.Services;

namespace Craft.Realtime;

/// <summary>
/// In-memory realtime delivery for the host. Downstream code publishes job lifecycle events
/// (start/update/end) via <see cref="RealtimeBridge"/>; browsers consume them over SSE at
/// <c>/.craft/events</c>. Delivery is gated by identity: an event only reaches the user whose
/// server-resolved principal started the job, because a subscription is the pair (userId, jobId) and
/// workers publish under the identity of the request that ran them.
///
/// One "current message" is stored per active (userId, jobId) so a reconnecting browser resyncs
/// instantly. Single instance, no backplane — the publisher and the SSE connection must be the same
/// process (combined role). See docs/realtime-bridge-plan.md.
///
/// Opt-in: off unless <c>App:Realtime:Enabled=true</c> (or <c>CRAFT_REALTIME_ENABLED=true</c>). While off
/// the endpoint is not mapped, publishes are dropped, and no state or timer is held.
/// </summary>
public sealed class RealtimeService : IDisposable
{
    private readonly RealtimeSettings _cfg;
    private readonly bool _enabled;
    private readonly ILogger<RealtimeService> _logger;
    private readonly Timer? _sweep;

    // Subscription matrix: "userId\0jobId" -> current message. The stored value is the ready-to-write
    // SSE frame, so reconnect replay is a direct copy.
    private readonly ConcurrentDictionary<string, Entry> _matrix = new(StringComparer.Ordinal);

    // Live SSE connections, grouped by userId (a user may have several tabs).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Connection>> _conns =
        new(StringComparer.OrdinalIgnoreCase);

    // Watch grants: jobId -> the users allowed to receive that job's events. Granted server-side by the
    // app, from the authenticated request that started the job; the browser never names a job itself.
    private readonly ConcurrentDictionary<string, JobWatch> _watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer? _runPump;
    private int _pumping;

    private static readonly TimeSpan RunPumpInterval = TimeSpan.FromSeconds(3);

    private long _seq;
    private int _connectionCount;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RealtimeService(CraftSettings settings, ILogger<RealtimeService> logger)
    {
        _cfg = settings.Realtime;
        _enabled = _cfg.IsEnabled; // resolved once — config/env don't change after startup
        _logger = logger;
        if (!_enabled) return; // opt-in feature is off: no state to sweep, so no timer

        var period = TimeSpan.FromMinutes(Math.Clamp(_cfg.EntryTtlMinutes, 1, 1440) / 2.0 + 0.5);
        _sweep = new Timer(_ => SweepExpired(), null, period, period);
        _runPump = new Timer(_ => PumpRuns(), null, RunPumpInterval, RunPumpInterval);
    }

    /// <summary>A watched run that has not appeared by then is never going to: stop reading run status for it.</summary>
    internal TimeSpan RunStartGrace { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Run-status source for the pump; swapped in tests.</summary>
    internal Func<IReadOnlyCollection<string>, Dictionary<string, QueueStatusBridge.RunRollup>> RunStatusSource { get; set; } =
        QueueStatusBridge.GetRunRollups;

    private sealed class JobWatch
    {
        public readonly ConcurrentDictionary<string, byte> Users = new(StringComparer.OrdinalIgnoreCase);
        public volatile bool TrackRun;
        public string? LastRunState;
        public readonly long CreatedTimestamp = Stopwatch.GetTimestamp();
        public long UpdatedTimestamp = Stopwatch.GetTimestamp();
    }

    public bool Enabled => _enabled;

    private sealed class Entry
    {
        public required string UserId { get; init; }
        public required string Frame { get; set; }
        public long UpdatedTimestamp { get; set; }
    }

    /// <summary>A single SSE connection — a bounded, coalescing queue of ready-to-write frames.</summary>
    public sealed class Connection
    {
        private readonly Channel<string> _ch;
        public Connection(int capacity)
        {
            _ch = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(8, capacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }
        public ChannelReader<string> Reader => _ch.Reader;
        public void Enqueue(string frame) => _ch.Writer.TryWrite(frame);
        public void Complete() => _ch.Writer.TryComplete();
    }

    private static string Key(string userId, string jobId) => userId + "\0" + jobId;

    // ── Publish ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publish a job event. <paramref name="userId"/> and <paramref name="jobId"/> (see <see cref="IsSafeJobId"/>)
    /// are required; everything else is optional. Best-effort and non-throwing.
    /// </summary>
    public void Publish(string userId, string jobId, string? mode, object? data,
        string? urlHref, string? urlLabel, int? status, string? message)
    {
        if (!_enabled) return;
        if (string.IsNullOrWhiteSpace(userId) || !IsSafeJobId(jobId, _logger)) return;

        var m = NormalizeMode(mode);
        var outStatus = status;
        var outMessage = message;
        string? dataJson = null;
        var oversized = false;

        if (data != null)
        {
            try { dataJson = JsonSerializer.Serialize(Normalize(data), s_json); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Realtime] Failed to serialize data for job {JobId}", jobId);
            }

            if (dataJson != null && Encoding.UTF8.GetByteCount(dataJson) > _cfg.MaxMessageBytes)
            {
                dataJson = null;
                oversized = true;
                outStatus = 413;
                outMessage = $"message too large (>{_cfg.MaxMessageBytes} bytes) — dropped";
            }
        }

        var seq = Interlocked.Increment(ref _seq);
        var frame = BuildFrame(jobId, m, seq, outStatus, outMessage, urlHref, urlLabel, dataJson);
        var key = Key(userId, jobId);

        if (!oversized)
        {
            // Store the delivered frame as the current message for reconnect replay, "end" included so a tab
            // that dropped before the end still gets the final state; the TTL sweep evicts it. On oversize we
            // keep the previous good frame (do not overwrite it with a truncated marker).
            if (_matrix.ContainsKey(key) || _matrix.Count < _cfg.MaxActiveJobs)
            {
                _matrix[key] = new Entry
                {
                    UserId = userId,
                    Frame = frame,
                    UpdatedTimestamp = Stopwatch.GetTimestamp()
                };
            }
            else
            {
                _logger.LogWarning("[Realtime] MaxActiveJobs ({Max}) reached — not storing new job {JobId}",
                    _cfg.MaxActiveJobs, jobId);
            }
        }

        // Deliver live to every one of the user's connections (tabs).
        if (_conns.TryGetValue(userId, out var set))
            foreach (var c in set.Values)
                c.Enqueue(frame);
    }

    // ── Watches ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Grant <paramref name="userId"/> the events of <paramref name="jobId"/> (see <see cref="IsSafeJobId"/>). Call it from the
    /// authenticated request that started the job, with that request's principal name. With
    /// <paramref name="trackRun"/>, Craft also pushes the job's orchestrator run status itself, matched the
    /// same way as <see cref="QueueStatusBridge.GetRun"/>, until the run finishes.
    /// </summary>
    public void Watch(string userId, string jobId, bool trackRun)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(userId) || !IsSafeJobId(jobId, _logger)) return;

        if (!_watches.TryGetValue(jobId, out var watch))
        {
            if (_watches.Count >= _cfg.MaxActiveJobs)
            {
                _logger.LogWarning("[Realtime] MaxActiveJobs ({Max}) reached — not watching job {JobId}",
                    _cfg.MaxActiveJobs, jobId);
                return;
            }
            watch = _watches.GetOrAdd(jobId, _ => new JobWatch());
        }

        watch.Users[userId] = 0;
        watch.UpdatedTimestamp = Stopwatch.GetTimestamp();
        if (trackRun) watch.TrackRun = true;
    }

    /// <summary>Publish to every user granted <paramref name="jobId"/> through <see cref="Watch"/>.</summary>
    public void Notify(string jobId, string? mode, object? data)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(jobId) || !_watches.TryGetValue(jobId, out var watch)) return;
        watch.UpdatedTimestamp = Stopwatch.GetTimestamp();
        foreach (var userId in watch.Users.Keys)
            Publish(userId, jobId, mode, data, null, null, null, null);
    }

    /// <summary>
    /// Push the run status of every tracked watch whose counts changed since the last push. One read of
    /// the run summaries serves all watches, so the cost does not grow with the number of open tabs.
    /// </summary>
    internal void PumpRuns()
    {
        if (Interlocked.Exchange(ref _pumping, 1) == 1) return; // a slow storage read is still running
        try
        {
            var tracked = new List<string>();
            foreach (var kv in _watches)
                if (kv.Value.TrackRun) tracked.Add(kv.Key);
            if (tracked.Count == 0) return;

            var rollups = RunStatusSource(tracked);
            foreach (var jobId in tracked)
            {
                if (!_watches.TryGetValue(jobId, out var watch)) continue;
                if (!rollups.TryGetValue(jobId, out var run))
                {
                    if (Stopwatch.GetElapsedTime(watch.CreatedTimestamp) <= RunStartGrace) continue;
                    // Tell the watcher to stop waiting rather than leave a tracker that no longer polls hanging.
                    watch.TrackRun = false;
                    Notify(jobId, "end", new Dictionary<string, object?> { ["status"] = "NotFound" });
                    continue;
                }

                if (run.State == watch.LastRunState) continue;
                watch.LastRunState = run.State;

                var finished = run.Status is "Completed" or "CompletedWithErrors";
                if (finished) watch.TrackRun = false;
                Notify(jobId, finished ? "end" : "update", run.Data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Realtime] Run status pump failed");
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
    }

    private const int MaxJobIdLength = 128;

    /// <summary>
    /// A job id is any short token an app already uses (a GUID, <c>BEC-20261008-ab12</c>, a run name): 1-128
    /// letters, digits, <c>-</c>, <c>_</c>, <c>.</c> or <c>:</c>. Anything else is dropped, never thrown, and only
    /// its length is logged, so a hostile id cannot reach a log line, a key separator or the frame.
    /// </summary>
    internal static bool IsSafeJobId(string? jobId, ILogger? logger = null)
    {
        if (jobId is { Length: > 0 and <= MaxJobIdLength })
        {
            var safe = true;
            foreach (var c in jobId)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) { safe = false; break; }
            if (safe) return true;
        }
        logger?.LogWarning("[Realtime] Rejected a job id that is not a short token ({Length} chars)", jobId?.Length ?? 0);
        return false;
    }

    // ── SSE connection management ────────────────────────────────────────────────

    /// <summary>Register a new SSE connection for a user. Returns null id/connection if over the cap.</summary>
    public (Guid Id, Connection? Conn) Connect(string userId)
    {
        if (Interlocked.Increment(ref _connectionCount) > _cfg.MaxConnections)
        {
            Interlocked.Decrement(ref _connectionCount);
            _logger.LogWarning("[Realtime] MaxConnections ({Max}) reached — rejecting new stream", _cfg.MaxConnections);
            return (Guid.Empty, null);
        }

        var conn = new Connection(_cfg.PerConnectionQueue);
        var set = _conns.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Connection>());
        var id = Guid.NewGuid();
        set[id] = conn;
        return (id, conn);
    }

    public void Disconnect(string userId, Guid id)
    {
        if (id == Guid.Empty) return;
        if (_conns.TryGetValue(userId, out var set) && set.TryRemove(id, out var conn))
        {
            conn.Complete();
            Interlocked.Decrement(ref _connectionCount);
            if (set.IsEmpty) _conns.TryRemove(userId, out _);
        }
    }

    /// <summary>Current stored frames for a user, for replay on (re)connect.</summary>
    public IEnumerable<string> CurrentFrames(string userId)
    {
        foreach (var e in _matrix.Values)
            if (string.Equals(e.UserId, userId, StringComparison.OrdinalIgnoreCase))
                yield return e.Frame;
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    private void SweepExpired()
    {
        try
        {
            var ttl = TimeSpan.FromMinutes(Math.Clamp(_cfg.EntryTtlMinutes, 1, 1440));
            foreach (var kv in _matrix)
                if (Stopwatch.GetElapsedTime(kv.Value.UpdatedTimestamp) > ttl)
                    _matrix.TryRemove(kv.Key, out _);
            foreach (var kv in _watches)
                if (!kv.Value.TrackRun && Stopwatch.GetElapsedTime(kv.Value.UpdatedTimestamp) > ttl)
                    _watches.TryRemove(kv.Key, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Realtime] TTL sweep failed");
        }
    }

    private static string BuildFrame(string jobId, string mode, long seq, int? status, string? message,
        string? urlHref, string? urlLabel, string? dataJson)
    {
        var sb = new StringBuilder(160);
        sb.Append("{\"jobId\":").Append(JsonSerializer.Serialize(jobId));
        sb.Append(",\"mode\":").Append(JsonSerializer.Serialize(mode));
        sb.Append(",\"seq\":").Append(seq);
        sb.Append(",\"ts\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (status.HasValue) sb.Append(",\"status\":").Append(status.Value);
        if (!string.IsNullOrEmpty(message)) sb.Append(",\"message\":").Append(JsonSerializer.Serialize(message));
        if (!string.IsNullOrEmpty(urlHref))
        {
            sb.Append(",\"url\":{\"href\":").Append(JsonSerializer.Serialize(urlHref));
            if (!string.IsNullOrEmpty(urlLabel)) sb.Append(",\"label\":").Append(JsonSerializer.Serialize(urlLabel));
            sb.Append('}');
        }
        if (dataJson != null) sb.Append(",\"data\":").Append(dataJson);
        sb.Append('}');
        // SSE frame: id enables Last-Event-ID resume; blank line terminates the event.
        return $"id: {seq}\ndata: {sb}\n\n";
    }

    private static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "start" => "start",
        "end" => "end",
        _ => "update"
    };

    /// <summary>Convert PowerShell (Hashtable/PSObject) and CLR values into STJ-serializable shapes.</summary>
    private static object? Normalize(object? v) => v switch
    {
        null => null,
        string or bool or int or long or double or float or decimal or DateTime or DateTimeOffset or Guid => v,
        JsonElement => v,
        PSObject { BaseObject: PSCustomObject } ps => NormalizeProperties(ps),
        PSObject ps => Normalize(ps.BaseObject),
        IDictionary d => NormalizeDict(d),
        IEnumerable e => NormalizeList(e),
        _ => v.ToString()
    };

    private static Dictionary<string, object?> NormalizeDict(IDictionary d)
    {
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in d)
            r[e.Key?.ToString() ?? ""] = Normalize(e.Value);
        return r;
    }

    // A [pscustomobject] (and anything ConvertFrom-Json returns) has no CLR shape of its own: its note properties are the data.
    private static Dictionary<string, object?> NormalizeProperties(PSObject ps)
    {
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in ps.Properties)
            r[p.Name] = Normalize(p.Value);
        return r;
    }

    private static List<object?> NormalizeList(IEnumerable e)
    {
        var r = new List<object?>();
        foreach (var i in e) r.Add(Normalize(i));
        return r;
    }

    public void Dispose()
    {
        _sweep?.Dispose();
        _runPump?.Dispose();
    }
}
