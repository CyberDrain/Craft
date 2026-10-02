using System.Globalization;
using System.Text.Json;
using Craft.Configuration;
using Craft.Storage;

namespace Craft.Hosting;

/// <summary>
/// Instance-wide daily egress counter for app-only API clients, with per-client accounting. The running
/// daily totals are the source of truth for the cap and are persisted to a small local file so a restart
/// does not reset the day; the time-bucketed history and a durable daily audit are additionally mirrored
/// to a table so the product can show usage over time and whether/when/how often the cap was hit.
/// Each response is also attributed to its endpoint (daily per client, and per instance bucket), and
/// interactive user traffic is tracked the same way under its own partition without ever counting
/// toward the cap.
/// <para>
/// <b>Request path</b> (<see cref="Record"/> / <see cref="RecordShed"/>) only touches memory under a short
/// lock — no IO. A background timer flushes the daily totals to disk and mirrors the current 15-minute
/// bucket plus today's daily summary to the table every <c>FlushSeconds</c> (and on shutdown), and only
/// when something changed. <b>Restart</b> reloads the daily totals from the file (so the cap is correct
/// immediately) and reseeds the current in-progress bucket from the table (so its row does not regress).
/// A periodic purge drops table rows past the retention window. Table failures never affect the cap — the
/// file is the truth; the table is a best-effort mirror.
/// </para>
/// <para>
/// The file lives with the log files (<see cref="FileLoggingSettings.ResolvedDirectory"/>), the writable
/// area confirmed to survive restarts. The table is written through Craft's own store — the same storage
/// account the hosted app reads with Get-CIPPTable — so CIPP queries it directly. Layout: see
/// <see cref="EgressTableSchema"/>.
/// </para>
/// </summary>
public sealed class EgressLedger : BackgroundService
{
    private readonly ILogger<EgressLedger> _logger;
    private readonly long _capBytes;
    private readonly int _flushSeconds;
    private readonly string _filePath;
    private readonly Func<DateTime> _utcNow;

    // Table mirror — null store or blank table name = file-only (no mirror).
    private readonly ICraftTableStore? _store;
    private readonly string _tableName;
    private readonly int _bucketMinutes;
    private readonly int _retentionDays;
    private bool TableEnabled => _store is not null && !string.IsNullOrWhiteSpace(_tableName);

    private readonly object _lock = new();
    private DateOnly _dateUtc;
    private long _bytes;
    private long _shedRequests;
    private DateTime? _capReachedUtc;
    private bool _dirty;         // file needs rewriting
    private bool _tableDirty;    // something changed since the last table sync

    private readonly Dictionary<string, ClientTotals> _clients = new(StringComparer.OrdinalIgnoreCase);
    // Interactive (user) traffic: reported in its own partition, never part of _bytes or the cap.
    private ClientTotals _interactive = new();
    // Pending table buckets not yet finalised: bucketRowKey -> (partition -> accum). Partitions are the
    // API clients' AppIds, the instance-total aggregate and the interactive partition; only the latter
    // two carry an endpoint breakdown.
    private readonly Dictionary<string, Dictionary<string, BucketAccum>> _buckets = new(StringComparer.Ordinal);

    private bool _tableReady;

    private sealed class ClientTotals
    {
        public long Bytes; public long Requests; public long Shed; public DateTime LastSeenUtc;
        public Dictionary<string, EndpointStats> Endpoints = new(StringComparer.OrdinalIgnoreCase);
        public ClientTotals Clone() => new() { Bytes = Bytes, Requests = Requests, Shed = Shed, LastSeenUtc = LastSeenUtc, Endpoints = EndpointStats.CloneMap(Endpoints) };
    }

    private sealed class BucketAccum
    {
        public long Bytes; public long Requests; public long Shed;
        public Dictionary<string, EndpointStats> Endpoints = new(StringComparer.OrdinalIgnoreCase);
        public BucketAccum Clone() => new() { Bytes = Bytes, Requests = Requests, Shed = Shed, Endpoints = EndpointStats.CloneMap(Endpoints) };
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Production constructor — resolves settings and injects the shared table store.</summary>
    public EgressLedger(ILogger<EgressLedger> logger, CraftSettings settings, ICraftTableStore store)
        : this(logger,
               (settings ?? throw new ArgumentNullException(nameof(settings))).RateLimit.Egress.ResolvedBytesPerDay,
               settings.RateLimit.Egress.ResolvedFlushSeconds,
               Path.Combine(settings.FileLogging.ResolvedDirectory, "egress-ledger.json"),
               utcNow: null,
               store: store,
               tableName: settings.RateLimit.Egress.ResolvedTableName,
               bucketMinutes: settings.RateLimit.Egress.ResolvedBucketMinutes,
               retentionDays: settings.RateLimit.Egress.ResolvedRetentionDays)
    {
    }

    /// <summary>Test/explicit constructor. <paramref name="utcNow"/> defaults to the real clock;
    /// <paramref name="store"/> null (or a blank <paramref name="tableName"/>) = file-only.</summary>
    internal EgressLedger(ILogger<EgressLedger> logger, long capBytes, int flushSeconds, string filePath,
        Func<DateTime>? utcNow = null, ICraftTableStore? store = null, string tableName = "",
        int bucketMinutes = 15, int retentionDays = 7)
    {
        _logger = logger;
        _capBytes = Math.Max(0, capBytes);
        _flushSeconds = Math.Max(1, flushSeconds);
        _filePath = filePath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _store = store;
        _tableName = tableName ?? string.Empty;
        _bucketMinutes = Math.Max(1, bucketMinutes);
        _retentionDays = Math.Max(1, retentionDays);
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

    /// <summary>Add the outbound bytes of one API-client response to today's running totals, attributed to
    /// the client and, in its daily and the instance bucket breakdowns, to <paramref name="endpoint"/>.</summary>
    public void Record(long bytes, string appId, string? endpoint = null, bool cacheHit = false, int statusCode = 200)
    {
        if (bytes <= 0) return;
        appId = Normalize(appId);
        var label = EgressTableSchema.EndpointLabel(endpoint);
        lock (_lock)
        {
            RolloverIfNeeded();
            _bytes += bytes;
            var client = Client(appId);
            client.Bytes += bytes;
            client.Requests += 1;
            client.LastSeenUtc = _utcNow();
            EndpointStats.In(client.Endpoints, label).Add(bytes, cacheHit, statusCode);
            var bucket = Bucket(appId);
            bucket.Bytes += bytes;
            bucket.Requests += 1;
            var system = Bucket(EgressTableSchema.SystemPartition);
            system.Bytes += bytes;
            system.Requests += 1;
            EndpointStats.In(system.Endpoints, label).Add(bytes, cacheHit, statusCode);
            _dirty = true;
            _tableDirty = true;
        }
    }

    /// <summary>Add the outbound bytes of one interactive (user) response. Reported under
    /// <see cref="EgressTableSchema.InteractivePartition"/>; never counts toward the cap.</summary>
    public void RecordInteractive(long bytes, string? endpoint = null, bool cacheHit = false, int statusCode = 200)
    {
        if (bytes <= 0) return;
        var label = EgressTableSchema.EndpointLabel(endpoint);
        lock (_lock)
        {
            RolloverIfNeeded();
            _interactive.Bytes += bytes;
            _interactive.Requests += 1;
            _interactive.LastSeenUtc = _utcNow();
            EndpointStats.In(_interactive.Endpoints, label).Add(bytes, cacheHit, statusCode);
            var bucket = Bucket(EgressTableSchema.InteractivePartition);
            bucket.Bytes += bytes;
            bucket.Requests += 1;
            EndpointStats.In(bucket.Endpoints, label).Add(bytes, cacheHit, statusCode);
            _dirty = true;
            _tableDirty = true;
        }
    }

    /// <summary>Record that a request from <paramref name="appId"/> was shed (429) once over budget. The
    /// shed response body is deliberately not counted as bytes; this tracks the cap-hit count.</summary>
    public void RecordShed(string appId, string? endpoint = null)
    {
        appId = Normalize(appId);
        var label = EgressTableSchema.EndpointLabel(endpoint);
        lock (_lock)
        {
            RolloverIfNeeded();
            _shedRequests += 1;
            _capReachedUtc ??= _utcNow();
            var client = Client(appId);
            client.Shed += 1;
            EndpointStats.In(client.Endpoints, label).Shed += 1;
            Bucket(appId).Shed += 1;
            var system = Bucket(EgressTableSchema.SystemPartition);
            system.Shed += 1;
            EndpointStats.In(system.Endpoints, label).Shed += 1;
            _dirty = true;
            _tableDirty = true;
        }
    }

    /// <summary>Bytes served so far today (for logging / a metrics surface).</summary>
    public long CurrentBytes
    {
        get { lock (_lock) { RolloverIfNeeded(); return _bytes; } }
    }

    /// <summary>Requests shed (429) so far today.</summary>
    public long ShedRequests
    {
        get { lock (_lock) { RolloverIfNeeded(); return _shedRequests; } }
    }

    /// <summary>Seconds from now until the next UTC midnight, floored at 1.</summary>
    public int SecondsToNextUtcMidnight()
    {
        var now = _utcNow();
        return Math.Max(1, (int)Math.Ceiling((now.Date.AddDays(1) - now).TotalSeconds));
    }

    /// <summary>ISO-8601 timestamp of the next UTC midnight, for the rejection body.</summary>
    public string NextUtcMidnightIso() =>
        _utcNow().Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    // Must be called under _lock.
    private ClientTotals Client(string appId)
    {
        if (!_clients.TryGetValue(appId, out var c)) { c = new ClientTotals(); _clients[appId] = c; }
        return c;
    }

    // Must be called under _lock. Returns the accumulator for appId in the current 15-min bucket.
    private BucketAccum Bucket(string appId)
    {
        var key = EgressTableSchema.BucketRowKey(EgressTableSchema.BucketStart(_utcNow(), _bucketMinutes));
        if (!_buckets.TryGetValue(key, out var b)) { b = new Dictionary<string, BucketAccum>(StringComparer.OrdinalIgnoreCase); _buckets[key] = b; }
        if (!b.TryGetValue(appId, out var a)) { a = new BucketAccum(); b[appId] = a; }
        return a;
    }

    private static string Normalize(string? appId) => string.IsNullOrWhiteSpace(appId) ? "unknown" : appId.Trim();

    // Must be called under _lock.
    private void RolloverIfNeeded()
    {
        var today = DateOnly.FromDateTime(_utcNow());
        if (today == _dateUtc) return;
        _dateUtc = today;
        _bytes = 0;
        _shedRequests = 0;
        _capReachedUtc = null;
        _clients.Clear();
        _interactive = new ClientTotals();
        _buckets.Clear();       // yesterday's bucket rows stay in the table until retention purges them
        _dirty = true;
        _tableDirty = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[Egress] API egress accounting active — cap {Cap}, flush every {Flush}s, file {File}, table {Table}",
            _capBytes > 0 ? $"{_capBytes} bytes/day" : "accounting only (no cap)",
            _flushSeconds,
            _filePath,
            TableEnabled ? $"{_tableName} ({_bucketMinutes}-min buckets, {_retentionDays}-day retention)" : "off (file only)");

        if (TableEnabled)
            await InitTableAsync(stoppingToken).ConfigureAwait(false);

        var lastPurgeDay = DateOnly.FromDateTime(_utcNow());
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_flushSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Flush();
                if (TableEnabled)
                {
                    await SyncToTableAsync(stoppingToken).ConfigureAwait(false);
                    var today = DateOnly.FromDateTime(_utcNow());
                    if (today != lastPurgeDay) { await PurgeAsync(stoppingToken).ConfigureAwait(false); lastPurgeDay = today; }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down — fall through to the final flush/sync.
        }

        Flush();
        if (TableEnabled)
        {
            try { await SyncToTableAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Egress] Final table sync failed"); }
        }
    }

    private async Task InitTableAsync(CancellationToken ct)
    {
        try
        {
            await _store!.EnsureTableAsync(_tableName, ct).ConfigureAwait(false);
            _tableReady = true;
            await SeedCurrentBucketAsync(ct).ConfigureAwait(false);   // backfill so a mid-bucket restart does not regress
            await PurgeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Table init failed — mirroring will retry on the next flush");
        }
    }

    /// <summary>Load the persisted daily totals. Restores only when the file is stamped with today's UTC
    /// date; a stale or unreadable file leaves today starting from zero. Tolerates the v1 and v2 formats.</summary>
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
            if (!sameDay || state.Bytes < 0)
            {
                _logger.LogInformation("[Egress] Ledger file not for today ({Stored}) — starting the day fresh", state.DateUtc);
                return;
            }

            lock (_lock)
            {
                _dateUtc = today;
                _bytes = state.Bytes;
                _shedRequests = Math.Max(0, state.ShedRequests);
                _capReachedUtc = state.CapReachedUtc;
                _clients.Clear();
                if (state.Clients is not null)
                {
                    foreach (var (appId, c) in state.Clients)
                        _clients[appId] = c.ToTotals();
                }
                _interactive = state.Interactive?.ToTotals() ?? new ClientTotals();
                _dirty = false;
            }
            _logger.LogInformation("[Egress] Restored {Bytes} bytes across {Clients} client(s) already served today from {File}",
                state.Bytes, state.Clients?.Count ?? 0, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Failed to load ledger — starting fresh");
        }
    }

    /// <summary>Rewrite the daily totals to disk via a temp file + atomic rename, only when changed.</summary>
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
                    Version = 3,
                    DateUtc = _dateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Bytes = _bytes,
                    CapBytes = _capBytes,
                    CapReachedUtc = _capReachedUtc,
                    ShedRequests = _shedRequests,
                    UpdatedUtc = _utcNow(),
                    Clients = _clients.ToDictionary(kv => kv.Key, kv => ClientState.From(kv.Value)),
                    Interactive = _interactive.Requests > 0 ? ClientState.From(_interactive) : null,
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
            lock (_lock) { _dirty = true; }
        }
    }

    // ── Table mirror ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mirror the current bucket(s) and today's daily summary to the table. Only rows for clients
    /// active since the last sync are written. Finalised buckets (older than the current one) are written
    /// then dropped from memory; the current bucket stays and is re-written as it grows.</summary>
    internal async Task SyncToTableAsync(CancellationToken ct)
    {
        if (!_tableReady) { try { await _store!.EnsureTableAsync(_tableName, ct).ConfigureAwait(false); _tableReady = true; } catch { return; } }

        // Snapshot under the lock.
        DateOnly day; long dayBytes, dayRequests, dayShed; DateTime? capReached; bool enforcing;
        string currentBucketKey;
        List<(string bucketKey, Dictionary<string, BucketAccum> partitions)> buckets;
        Dictionary<string, ClientTotals> clientDaily;
        ClientTotals? interactiveDaily;
        var instanceEndpoints = new Dictionary<string, EndpointStats>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (!_tableDirty && !_buckets.Keys.Any(k => k != CurrentBucketKey())) return;
            RolloverIfNeeded();
            currentBucketKey = CurrentBucketKey();
            buckets = _buckets.Select(kv => (kv.Key, kv.Value.ToDictionary(c => c.Key, c => c.Value.Clone(), StringComparer.OrdinalIgnoreCase))).ToList();
            var active = new HashSet<string>(buckets.SelectMany(b => b.partitions.Keys), StringComparer.OrdinalIgnoreCase);
            clientDaily = active.Where(a => _clients.ContainsKey(a)).ToDictionary(a => a, a => _clients[a].Clone(), StringComparer.OrdinalIgnoreCase);
            interactiveDaily = active.Contains(EgressTableSchema.InteractivePartition) ? _interactive.Clone() : null;
            foreach (var client in _clients.Values)
                foreach (var (label, stats) in client.Endpoints)
                {
                    if (!instanceEndpoints.TryGetValue(label, out var sum)) instanceEndpoints[label] = sum = new EndpointStats();
                    sum.Merge(stats);
                }
            day = _dateUtc; dayBytes = _bytes; dayRequests = _clients.Values.Sum(c => c.Requests); dayShed = _shedRequests;
            capReached = _capReachedUtc; enforcing = _capBytes > 0;
            _tableDirty = false;
        }

        try
        {
            // Bucket rows — per client, the instance aggregate and interactive; the latter two carry endpoints.
            foreach (var (bucketKey, partitions) in buckets)
            {
                var bucketStart = ParseBucketStart(bucketKey);
                foreach (var (partition, a) in partitions)
                {
                    var row = partition == EgressTableSchema.SystemPartition
                        ? EgressTableSchema.SystemBucketRow(bucketStart, a.Bytes, a.Requests, a.Shed, _capBytes, a.Endpoints)
                        : EgressTableSchema.ClientBucketRow(partition, bucketStart, a.Bytes, a.Requests, a.Shed, _capBytes, a.Endpoints);
                    await _store!.UpsertAsync(_tableName, row, ct).ConfigureAwait(false);
                }
            }

            // Daily audit rows — per active client, interactive, and the instance summary (cap, when hit, how many shed).
            foreach (var (appId, c) in clientDaily)
                await _store!.UpsertAsync(_tableName, EgressTableSchema.ClientDailyRow(appId, day, c.Bytes, c.Requests, c.Shed, _capBytes, c.Endpoints), ct).ConfigureAwait(false);
            if (interactiveDaily is not null)
                await _store!.UpsertAsync(_tableName, EgressTableSchema.ClientDailyRow(EgressTableSchema.InteractivePartition, day,
                    interactiveDaily.Bytes, interactiveDaily.Requests, 0, _capBytes, interactiveDaily.Endpoints), ct).ConfigureAwait(false);
            await _store!.UpsertAsync(_tableName, EgressTableSchema.SystemDailyRow(day, dayBytes, dayRequests, dayShed, _capBytes, capReached, enforcing, instanceEndpoints), ct).ConfigureAwait(false);

            // Drop finalised buckets we just wrote (strictly older than the current one).
            lock (_lock)
            {
                foreach (var (bucketKey, _) in buckets)
                    if (string.CompareOrdinal(bucketKey, currentBucketKey) < 0)
                        _buckets.Remove(bucketKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Table sync failed — will retry on the next flush");
            lock (_lock) { _tableDirty = true; }   // ensure a retry
        }
    }

    /// <summary>Reseed the current bucket's per-client accumulators from the table so a restart mid-bucket
    /// continues the row rather than overwriting it with a smaller value.</summary>
    internal async Task SeedCurrentBucketAsync(CancellationToken ct)
    {
        try
        {
            var key = CurrentBucketKey();
            var filter = $"RowKey eq '{key}'";
            var seeded = 0;
            await foreach (var row in _store!.QueryTableAsync(_tableName, filter, ct).ConfigureAwait(false))
            {
                if (row.RowKey != key) continue;  // re-apply the filter — the store may ignore it
                lock (_lock)
                {
                    // Added onto, not replacing, anything this process already recorded before the seed ran.
                    var a = Bucket(row.PartitionKey);
                    a.Bytes += AsLong(row[EgressTableSchema.PropBytes]);
                    a.Requests += AsLong(row[EgressTableSchema.PropRequests]);
                    a.Shed += AsLong(row[EgressTableSchema.PropShed]);
                    foreach (var (label, stats) in EgressTableSchema.ParseEndpoints(row.GetString(EgressTableSchema.PropEndpoints)))
                        EndpointStats.In(a.Endpoints, label).Merge(stats);
                }
                seeded++;
            }
            if (seeded > 0) _logger.LogInformation("[Egress] Reseeded current bucket from table ({Count} rows)", seeded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Current-bucket backfill failed — the current partial bucket may under-count until the next boundary");
        }
    }

    /// <summary>Delete bucket and daily rows older than the retention window, partition by partition.</summary>
    internal async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            var now = _utcNow();
            var bucketCutoff = EgressTableSchema.BucketRetentionCutoffRowKey(now, _retentionDays, _bucketMinutes);
            var dailyCutoff = EgressTableSchema.DailyRetentionCutoffRowKey(now, _retentionDays);

            // One filtered scan for expired rows of either kind; group by partition and batch-delete.
            var filter = $"(RowKey ge '{EgressTableSchema.BucketPrefix}' and RowKey lt '{bucketCutoff}') or " +
                         $"(RowKey ge '{EgressTableSchema.DailyPrefix}' and RowKey lt '{dailyCutoff}')";
            var byPartition = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            await foreach (var row in _store!.QueryTableAsync(_tableName, filter, ct).ConfigureAwait(false))
            {
                var rk = row.RowKey;
                var expired = (rk.StartsWith(EgressTableSchema.BucketPrefix, StringComparison.Ordinal) && string.CompareOrdinal(rk, bucketCutoff) < 0)
                           || (rk.StartsWith(EgressTableSchema.DailyPrefix, StringComparison.Ordinal) && string.CompareOrdinal(rk, dailyCutoff) < 0);
                if (!expired) continue;   // re-apply the predicate — the store may ignore the filter
                if (!byPartition.TryGetValue(row.PartitionKey, out var list)) { list = new List<string>(); byPartition[row.PartitionKey] = list; }
                list.Add(rk);
            }

            var deleted = 0;
            foreach (var (pk, rowKeys) in byPartition)
            {
                await _store!.DeleteBatchAsync(_tableName, pk, rowKeys, ct).ConfigureAwait(false);
                deleted += rowKeys.Count;
            }
            if (deleted > 0) _logger.LogInformation("[Egress] Purged {Count} expired row(s) beyond {Days}-day retention", deleted, _retentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Retention purge failed — will retry on the next daily tick");
        }
    }

    private string CurrentBucketKey() =>
        EgressTableSchema.BucketRowKey(EgressTableSchema.BucketStart(_utcNow(), _bucketMinutes));

    private static DateTime ParseBucketStart(string bucketRowKey)
    {
        var stamp = bucketRowKey.AsSpan(EgressTableSchema.BucketPrefix.Length);   // yyyyMMddTHHmmssZ
        return DateTime.ParseExact(stamp, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    private static long AsLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        double d => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) => r,
        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var r) => r,
        _ => 0L,
    };

    private sealed class LedgerState
    {
        public int Version { get; set; }
        public string DateUtc { get; set; } = "";
        public long Bytes { get; set; }
        public long CapBytes { get; set; }
        public DateTime? CapReachedUtc { get; set; }
        public long ShedRequests { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public Dictionary<string, ClientState>? Clients { get; set; }
        public ClientState? Interactive { get; set; }
    }

    private sealed class ClientState
    {
        public long Bytes { get; set; }
        public long Requests { get; set; }
        public long Shed { get; set; }
        public DateTime LastSeenUtc { get; set; }
        // Same [Bytes, Requests, MaxBytes, CacheHits, Errors, Shed] layout as the table column.
        public Dictionary<string, long[]>? Endpoints { get; set; }

        public static ClientState From(ClientTotals t) => new()
        {
            Bytes = t.Bytes,
            Requests = t.Requests,
            Shed = t.Shed,
            LastSeenUtc = t.LastSeenUtc,
            Endpoints = t.Endpoints.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
        };

        public ClientTotals ToTotals()
        {
            var t = new ClientTotals { Bytes = Bytes, Requests = Requests, Shed = Shed, LastSeenUtc = LastSeenUtc };
            foreach (var (label, values) in Endpoints ?? [])
                t.Endpoints[label] = EndpointStats.FromArray(values);
            return t;
        }
    }
}
