using System.Collections;
using System.Globalization;

namespace Craft.Hosting;

/// <summary>
/// Normalises the <c>Headers</c> hashtable a PowerShell handler hangs off its
/// <c>HttpResponseContext</c> into something safe to put on the wire, and applies it to a response.
/// <para>
/// Until this existed, <c>Headers</c> and <c>ContentType</c> were declared on the response type and
/// then dropped on the floor by the extractor — a handler returning <c>302</c> with a
/// <c>Location</c> got a bodiless-looking 302 with no <c>Location</c>, which browsers render instead
/// of following. Anything that needs a header other than the ones CRAFT sets itself goes through
/// here.
/// </para>
/// <para>
/// Handler header values are attacker-influenced in practice — CIPP's SharePoint redirect builds its
/// <c>Location</c> out of a Graph response — so values are stripped of CR/LF before they reach
/// Kestrel, and the headers the server computes for itself are dropped rather than allowed to
/// corrupt the response framing.
/// </para>
/// </summary>
public static class HandlerHeaders
{
    /// <summary>What a response is served as when the handler does not say.</summary>
    public const string DefaultContentType = "application/json";

    /// <summary>
    /// Headers a handler must not set. Kestrel derives every one of these from the response itself;
    /// a handler-supplied value is either ignored or desyncs the connection. Dropped silently — a
    /// handler setting <c>Content-Length</c> is a mistake, not a request worth honouring.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Content-Length", "Date", "Host", "Keep-Alive", "Server",
        "Transfer-Encoding", "Upgrade",
    };

    /// <summary>Falls back to <see cref="DefaultContentType"/> for a handler that set no content type.</summary>
    public static string ResolveContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType;

    /// <summary>
    /// Whether a body may accompany this status (RFC 9110 §6.4.1). Writing one on a 204/304 either
    /// throws in Kestrel or desyncs the connection, so the dispatcher checks before writing.
    /// </summary>
    public static bool AllowsBody(int statusCode) =>
        statusCode is not 204 and not 304 and (< 100 or >= 200);

    /// <summary>
    /// Flattens a PowerShell <c>Headers</c> dictionary into name/value pairs, dropping reserved names
    /// and anything that normalises to empty. Returns <see langword="null"/> when nothing usable is
    /// left, so the overwhelmingly common case (no custom headers) allocates nothing and stores
    /// nothing in the cache.
    /// </summary>
    /// <remarks>
    /// A null or empty value is dropped rather than emitted as an empty header. That is deliberate:
    /// <c>@{ Location = $null }</c> is a handler bug, and an empty <c>Location</c> is
    /// indistinguishable on the wire from the missing-header bug this class exists to fix.
    /// </remarks>
    public static Dictionary<string, string>? FromPowerShell(IDictionary? headers)
    {
        if (headers is null || headers.Count == 0) return null;

        Dictionary<string, string>? result = null;

        foreach (DictionaryEntry entry in headers)
        {
            var name = Sanitize(Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
            if (name is null || Reserved.Contains(name)) continue;

            var value = Flatten(entry.Value);
            if (value is null) continue;

            result ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result[name] = value;
        }

        return result;
    }

    /// <summary>
    /// Writes normalised handler headers onto a response. Call this before CRAFT's own diagnostics
    /// headers so a handler cannot spoof <c>X-Cache</c> and friends.
    /// </summary>
    public static void Apply(HttpResponse response, IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (headers is null) return;

        foreach (var (name, value) in headers)
        {
            // Content-Type is not a plain header on HttpResponse — assigning it through the header
            // collection fights the ContentType property rather than replacing it.
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                response.ContentType = value;
            else
                response.Headers[name] = value;
        }
    }

    /// <summary>
    /// Collapses one header value. Arrays are joined with ", " — correct for every standard header
    /// except <c>Set-Cookie</c>, which handlers should not be minting from PowerShell anyway.
    /// </summary>
    private static string? Flatten(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return Sanitize(s);
            case IEnumerable seq:
                var parts = new List<string>();
                foreach (var item in seq)
                {
                    var part = Sanitize(Convert.ToString(item, CultureInfo.InvariantCulture));
                    if (part is not null) parts.Add(part);
                }
                return parts.Count == 0 ? null : string.Join(", ", parts);
            default:
                return Sanitize(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Trims, strips CR/LF (response splitting), and maps "nothing left" to null.</summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var cleaned = value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }
}
