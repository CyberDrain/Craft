using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Craft.Hosting;

/// <summary>
/// Tracks slow HTTP requests via DiagnosticListener (method, URL, body) and
/// DNS/TLS/Socket timing via EventSource.
/// </summary>
public sealed class HttpDiagnosticListener : EventListener,
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>
{
    private readonly ILogger _logger;
    private readonly int _slowThresholdMs;

    // In-flight HTTP requests: stores only lightweight primitives (method, URL, body preview),
    // NOT the live HttpRequestMessage — holding a reference to that prevents GC of the entire
    // request pipeline (content streams, TLS buffers, socket state) for the request's duration.
    private readonly ConcurrentDictionary<string, (string Method, string Url, string? BodyPreview, long StartTicks, string? OpId)> _inflight = new();

    // Track DNS resolution timing
    private readonly ConcurrentDictionary<string, long> _dnsInFlight = new();

    // Track TLS handshake timing
    private readonly ConcurrentDictionary<string, long> _tlsInFlight = new();

    // Track socket connect timing
    private readonly ConcurrentDictionary<string, long> _connectInFlight = new();

    private IDisposable? _allListenersSubscription;
    private IDisposable? _httpListenerSubscription;

    // Cached PropertyInfo for extracting Request from anonymous diagnostic payload
    private PropertyInfo? _requestProperty;

    public HttpDiagnosticListener(ILogger logger, int slowThresholdMs = 1000)
    {
        _logger = logger;
        _slowThresholdMs = slowThresholdMs;
        _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        switch (eventSource.Name)
        {
            case "System.Net.NameResolution":
            case "System.Net.Security":
            case "System.Net.Sockets":
                EnableEvents(eventSource, EventLevel.Informational);
                break;
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        var key = Activity.Current?.Id ?? $"thread-{Environment.CurrentManagedThreadId}";

        switch (eventData.EventSource.Name)
        {
            case "System.Net.NameResolution":
                HandleDnsEvent(eventData, key);
                break;
            case "System.Net.Security":
                HandleTlsEvent(eventData, key);
                break;
            case "System.Net.Sockets":
                HandleSocketEvent(eventData, key);
                break;
        }
    }

    private void HandleDnsEvent(EventWrittenEventArgs e, string key)
    {
        // EventId 1 = ResolutionStart, EventId 2 = ResolutionStop
        if (e.EventId == 1)
        {
            _dnsInFlight[key] = System.Diagnostics.Stopwatch.GetTimestamp();
            var host = e.Payload?.Count > 0 ? e.Payload[0]?.ToString() : "?";
            _logger.LogDebug("[DNS-START] {Host}", host);
        }
        else if (e.EventId == 2)
        {
            if (_dnsInFlight.TryRemove(key, out var start))
            {
                var elapsedMs = GetElapsedMs(start);
                if (elapsedMs >= 50) // Log DNS taking more than 50ms
                {
                    var op = OperationContext.Display;
                    _logger.LogWarning("[DNS-SLOW] Resolution took {Elapsed}ms | {Op}",
                        elapsedMs, op);
                }
            }
        }
    }

    private void HandleTlsEvent(EventWrittenEventArgs e, string key)
    {
        // EventId 1 = HandshakeStart, EventId 2 = HandshakeStop
        if (e.EventId == 1)
        {
            _tlsInFlight[key] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        else if (e.EventId == 2)
        {
            if (_tlsInFlight.TryRemove(key, out var start))
            {
                var elapsedMs = GetElapsedMs(start);
                if (elapsedMs >= 100) // Log TLS taking more than 100ms
                {
                    var op = OperationContext.Display;
                    _logger.LogWarning("[TLS-SLOW] Handshake took {Elapsed}ms | {Op}",
                        elapsedMs, op);
                }
            }
        }
    }

    private void HandleSocketEvent(EventWrittenEventArgs e, string key)
    {
        // EventId 1 = ConnectStart, EventId 2 = ConnectStop
        if (e.EventId == 1)
        {
            _connectInFlight[key] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        else if (e.EventId == 2)
        {
            if (_connectInFlight.TryRemove(key, out var start))
            {
                var elapsedMs = GetElapsedMs(start);
                if (elapsedMs >= 100) // Log socket connect taking more than 100ms
                {
                    var op = OperationContext.Display;
                    _logger.LogWarning("[SOCKET-SLOW] Connect took {Elapsed}ms | {Op}",
                        elapsedMs, op);
                }
            }
        }
    }

    private static long GetElapsedMs(long startTicks)
    {
        return (Stopwatch.GetTimestamp() - startTicks) * 1000 / Stopwatch.Frequency;
    }

    private static bool IsStandardPort(string scheme, int port)
        => (scheme == "https" && port == 443) || (scheme == "http" && port == 80);

    // ── DiagnosticListener observer (HTTP-SLOW with method + body) ──────

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "HttpHandlerDiagnosticListener")
        {
            _httpListenerSubscription?.Dispose();
            _httpListenerSubscription = listener.Subscribe(this);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> pair)
    {
        try
        {
            switch (pair.Key)
            {
                case "System.Net.Http.HttpRequestOut.Start":
                    OnHttpStart(pair.Value);
                    break;
                case "System.Net.Http.HttpRequestOut.Stop":
                    OnHttpStop(pair.Value);
                    break;
            }
        }
        catch { /* never break HTTP pipeline */ }
    }

    private void OnHttpStart(object? payload)
    {
        var request = ExtractRequest(payload);
        if (request == null) return;

        var key = Activity.Current?.Id;
        if (key == null) return;

        string? bodyPreview = null;
        if (request.Content != null && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            try
            {
                var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                bodyPreview = SummarizeBody(body, request.RequestUri);
            }
            catch { /* stream may not be readable */ }
        }

        // Store only primitives — do NOT retain the HttpRequestMessage reference.
        // Holding it alive prevents GC of content streams, TLS buffers, and socket state.
        _inflight[key] = (request.Method.Method, request.RequestUri?.ToString() ?? "?", bodyPreview, Stopwatch.GetTimestamp(), OperationContext.Display);
    }

    private static readonly Regex s_batchOpRegex = new(@"(PUT|PATCH|DELETE|POST|MERGE)\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex s_partitionKeyRegex = new(@"PartitionKey='([^']*)'", RegexOptions.Compiled);
    private static readonly Regex s_rowKeyRegex = new(@"RowKey='([^']*)'", RegexOptions.Compiled);

    // Form-encoded keys whose values are credentials and must never reach the log.
    // OAuth covers most of these; `password` defends against ROPC-style flows.
    private static readonly HashSet<string> s_sensitiveFormKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "refresh_token",
        "client_secret",
        "assertion",
        "client_assertion",
        "code",
        "password",
    };

    private static readonly Regex s_formPairRegex = new(
        @"(?<sep>^|&)(?<key>[^=&]+)=(?<value>[^&]*)",
        RegexOptions.Compiled);

    private static string ScrubSensitiveFormValues(string body)
    {
        return s_formPairRegex.Replace(body, m =>
        {
            string key;
            try { key = Uri.UnescapeDataString(m.Groups["key"].Value); }
            catch { key = m.Groups["key"].Value; }
            return s_sensitiveFormKeys.Contains(key)
                ? $"{m.Groups["sep"].Value}{m.Groups["key"].Value}=***"
                : m.Value;
        });
    }

    private static string SummarizeBody(string? body, Uri? requestUri)
    {
        if (string.IsNullOrEmpty(body)) return "";

        // Detect Table Storage $batch requests
        if (requestUri?.AbsolutePath.EndsWith("/$batch", StringComparison.OrdinalIgnoreCase) == true)
        {
            var ops = s_batchOpRegex.Matches(body);
            if (ops.Count > 0)
            {
                var firstUrl = ops[0].Groups[2].Value;
                var table = ExtractTableName(firstUrl);
                var method = ops[0].Groups[1].Value;

                // Extract unique PartitionKeys and sample RowKeys
                var partitionKeys = new HashSet<string>();
                var rowKeys = new List<string>();
                foreach (Match op in ops)
                {
                    var url = op.Groups[2].Value;
                    var pk = s_partitionKeyRegex.Match(url);
                    if (pk.Success) partitionKeys.Add(pk.Groups[1].Value);
                    var rk = s_rowKeyRegex.Match(url);
                    if (rk.Success && rowKeys.Count < 3) rowKeys.Add(rk.Groups[1].Value);
                }

                var pkSummary = partitionKeys.Count switch
                {
                    0 => "",
                    1 => $" PK={partitionKeys.First()}",
                    _ => $" PK=[{string.Join(",", partitionKeys.Take(3))}{(partitionKeys.Count > 3 ? ",..." : "")}]"
                };
                var rkSample = rowKeys.Count > 0
                    ? $" RK=[{string.Join(",", rowKeys)}{(ops.Count > rowKeys.Count ? ",..." : "")}]"
                    : "";

                return $"$batch {ops.Count}x {method} → {table}{pkSummary}{rkSample}";
            }
            return "$batch (unparsed)";
        }

        // For regular POST/PUT bodies, scrub credentials before truncating so a
        // sensitive key past the 300-char cutoff still gets redacted.
        var scrubbed = ScrubSensitiveFormValues(body);
        if (scrubbed.Length > 300)
            return scrubbed[..300] + "...";
        return scrubbed;
    }

    private static string ExtractTableName(string url)
    {
        var path = url;
        try { path = new Uri(url).AbsolutePath; } catch { }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
        {
            var raw = segments[^1];
            var paren = raw.IndexOf('(');
            return paren > 0 ? raw[..paren] : raw;
        }
        return url;
    }

    private void OnHttpStop(object? payload)
    {
        var key = Activity.Current?.Id;
        if (key == null) return;

        if (_inflight.TryRemove(key, out var entry))
        {
            var elapsedMs = GetElapsedMs(entry.StartTicks);
            if (elapsedMs >= _slowThresholdMs)
            {
                var op = entry.OpId ?? OperationContext.Display;

                if (entry.BodyPreview != null)
                {
                    _logger.LogWarning("[HTTP-SLOW] {Url} took {Elapsed}ms | {Op} | {Method} | {Body}",
                        entry.Url, elapsedMs, op, entry.Method, entry.BodyPreview);
                }
                else
                {
                    _logger.LogWarning("[HTTP-SLOW] {Url} took {Elapsed}ms | {Op} | {Method}",
                        entry.Url, elapsedMs, op, entry.Method);
                }
            }
        }
    }

    private HttpRequestMessage? ExtractRequest(object? payload)
    {
        if (payload == null) return null;
        var prop = _requestProperty ??= payload.GetType()
            .GetProperty("Request", BindingFlags.Instance | BindingFlags.Public);
        return prop?.GetValue(payload) as HttpRequestMessage;
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    public override void Dispose()
    {
        _httpListenerSubscription?.Dispose();
        _allListenersSubscription?.Dispose();
        base.Dispose();
    }
}
