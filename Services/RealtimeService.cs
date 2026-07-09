using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Craft.Services;

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
/// </summary>
public sealed class RealtimeService : IDisposable
{
    private readonly RealtimeSettings _cfg;
    private readonly ILogger<RealtimeService> _logger;
    private readonly Timer _sweep;

    // Subscription matrix: "userId\0jobId" -> current message. The stored value is the ready-to-write
    // SSE frame, so reconnect replay is a direct copy.
    private readonly ConcurrentDictionary<string, Entry> _matrix = new(StringComparer.Ordinal);

    // Live SSE connections, grouped by userId (a user may have several tabs).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Connection>> _conns =
        new(StringComparer.OrdinalIgnoreCase);

    private long _seq;
    private int _connectionCount;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RealtimeService(CraftSettings settings, ILogger<RealtimeService> logger)
    {
        _cfg = settings.Realtime;
        _logger = logger;
        var period = TimeSpan.FromMinutes(Math.Clamp(_cfg.EntryTtlMinutes, 1, 1440) / 2.0 + 0.5);
        _sweep = new Timer(_ => SweepExpired(), null, period, period);
    }

    public bool Enabled => _cfg.Enabled;

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
    /// Publish a job event. <paramref name="userId"/> and <paramref name="jobId"/> (a GUID) are
    /// required; everything else is optional. Best-effort and non-throwing.
    /// </summary>
    public void Publish(string userId, string jobId, string? mode, object? data,
        string? urlHref, string? urlLabel, int? status, string? message)
    {
        if (!_cfg.Enabled) return;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(jobId)) return;
        if (!Guid.TryParse(jobId, out _))
        {
            _logger.LogWarning("[Realtime] Rejected non-GUID jobId '{JobId}'", jobId);
            return;
        }

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

        if (m == "end")
        {
            _matrix.TryRemove(key, out _);
        }
        else if (!oversized)
        {
            // Store the delivered frame as the current message for reconnect replay. On oversize we
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

    private static List<object?> NormalizeList(IEnumerable e)
    {
        var r = new List<object?>();
        foreach (var i in e) r.Add(Normalize(i));
        return r;
    }

    public void Dispose() => _sweep.Dispose();
}
