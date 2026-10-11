using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace Craft.Hosting;

/// <summary>
/// Tracks slow HTTP requests via DiagnosticListener (URL) and
/// DNS/TLS/Socket timing via EventSource.
/// </summary>
public sealed class HttpDiagnosticListener : EventListener,
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>
{
    private readonly ILogger _logger;
    private readonly int _slowThresholdMs;

    // In-flight HTTP requests: only the request URI and start time, NOT the live HttpRequestMessage or its
    // body. Holding the request alive prevents GC of content streams, TLS buffers and socket state, and reading
    // the body (as a preview for the slow log) buffered and copied every outgoing request.
    private readonly ConcurrentDictionary<string, (Uri? Url, long StartTicks)> _inflight = new();

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

    // ── DiagnosticListener observer (HTTP-SLOW) ──────

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

        _inflight[key] = (request.RequestUri, Stopwatch.GetTimestamp());
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
                _logger.LogWarning("[HTTP-SLOW] {Url} took {Elapsed}ms", entry.Url?.ToString() ?? "?", elapsedMs);
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
