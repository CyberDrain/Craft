using Craft.Configuration;

namespace Craft.Caching;

/// <summary>
/// Decides whether an individual request may touch the response cache at all — read or write.
/// <para>
/// This is a different question from the one <c>PowerShellDispatchEndpoint.IsCacheableRead</c> answers.
/// That one asks "is this handler a side-effect-free read" (the <c>List*</c> naming convention); this
/// one asks "is caching <em>this particular request</em> worth it". A read can be perfectly safe to
/// cache and still be a bad candidate: an endpoint that is already fast, or whose result depends on
/// who is asking rather than on the query string, gains nothing from the cache and risks colliding
/// with a differently-scoped caller under the same key.
/// </para>
/// <para>
/// Three opt-outs, all inert by default so an unconfigured Craft behaves exactly as it did before:
/// a required query parameter, values of that parameter that are too broad to cache, and a request
/// header that bypasses the cache for one call.
/// </para>
/// </summary>
public sealed class ResponseCachePolicy
{
    /// <summary>Policy that permits everything — the default when nothing is configured.</summary>
    public static readonly ResponseCachePolicy AllowAll = new("", null, "");

    private readonly string _requiredParam;
    private readonly HashSet<string> _excludedValues;
    private readonly string _noCacheHeader;

    // Excluded endpoints are split so the common case costs a hash lookup: literal names go in the set,
    // and only entries containing '*' are walked pattern by pattern.
    private readonly HashSet<string> _excludedEndpoints;
    private readonly string[] _excludedEndpointPatterns;

    // Values that mean "no" when they show up in the bypass header. Anything else non-empty means the
    // caller sent the header deliberately, so honour it rather than requiring one exact spelling.
    private static readonly HashSet<string> FalsyHeaderValues =
        new(StringComparer.OrdinalIgnoreCase) { "false", "0", "no" };

    public ResponseCachePolicy(
        string? requiredParam,
        IEnumerable<string>? excludedValues,
        string? noCacheHeader,
        IEnumerable<string>? excludedEndpoints = null)
    {
        _requiredParam = requiredParam ?? "";
        _noCacheHeader = noCacheHeader ?? "";
        _excludedValues = new HashSet<string>(excludedValues ?? [], StringComparer.OrdinalIgnoreCase);

        var endpoints = (excludedEndpoints ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .ToArray();
        _excludedEndpoints = new HashSet<string>(
            endpoints.Where(e => !e.Contains('*')), StringComparer.OrdinalIgnoreCase);
        _excludedEndpointPatterns = endpoints.Where(e => e.Contains('*')).ToArray();
    }

    public static ResponseCachePolicy FromSettings(CacheSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ResponseCachePolicy(
            settings.RequiredParam,
            settings.ExcludedParamValues,
            settings.NoCacheHeader,
            settings.ExcludedEndpoints);
    }

    /// <summary>Whether a required parameter is configured (i.e. the policy restricts anything by query).</summary>
    public bool HasRequiredParam => _requiredParam.Length > 0;

    /// <summary>The configured required parameter name, or an empty string when there is none.</summary>
    public string RequiredParam => _requiredParam;

    /// <summary>Number of configured excluded values — used to warn about values set without a param.</summary>
    public int ExcludedValueCount => _excludedValues.Count;

    /// <summary>Number of configured excluded endpoints, literal names and patterns together.</summary>
    public int ExcludedEndpointCount => _excludedEndpoints.Count + _excludedEndpointPatterns.Length;

    /// <summary>
    /// Whether an endpoint is excluded from caching outright by name or pattern. Checked ahead of the
    /// query-string rules: this is a flat "never cache this", so nothing on the request can rescue it.
    /// </summary>
    public bool IsEndpointExcluded(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return false;
        if (_excludedEndpoints.Contains(endpoint)) return true;

        foreach (var pattern in _excludedEndpointPatterns)
        {
            if (GlobMatches(pattern, endpoint)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether this request may participate in the cache.
    /// <paramref name="bypassReason"/> is null when it may, and otherwise a short token suitable for
    /// logging and for the <c>X-Cache-Bypass</c> response header.
    /// </summary>
    public bool Allows(string endpoint, HttpRequest request, out string? bypassReason)
    {
        ArgumentNullException.ThrowIfNull(request);
        bypassReason = null;

        if (IsEndpointExcluded(endpoint))
        {
            bypassReason = "excluded-endpoint";
            return false;
        }

        if (_noCacheHeader.Length > 0
            && request.Headers.TryGetValue(_noCacheHeader, out var headerValue))
        {
            var raw = headerValue.ToString();
            if (raw.Length > 0 && !FalsyHeaderValues.Contains(raw))
            {
                bypassReason = "no-cache-header";
                return false;
            }
        }

        if (_requiredParam.Length == 0)
            return true;

        if (!request.Query.TryGetValue(_requiredParam, out var values) || values.Count == 0)
        {
            bypassReason = "missing-required-param";
            return false;
        }

        // A repeated parameter is one request asking for several scopes at once; if any of them is
        // excluded or blank the whole request is, because the response covers all of them.
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                bypassReason = "empty-required-param";
                return false;
            }

            if (_excludedValues.Contains(value))
            {
                bypassReason = "excluded-param-value";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Case-insensitive glob match where <c>*</c> stands for any run of characters, including none.
    /// <para>
    /// Iterative with a single backtrack point rather than recursive. Patterns come from configuration
    /// and this runs on every request, so a pattern like <c>*a*a*a*a*</c> must not be able to blow the
    /// stack or go exponential — this form is linear in the common case and never recurses.
    /// </para>
    /// </summary>
    internal static bool GlobMatches(string pattern, string value)
    {
        int p = 0, v = 0, lastStar = -1, resumeAt = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                lastStar = p++;
                resumeAt = v;
            }
            else if (p < pattern.Length && CharsMatch(pattern[p], value[v]))
            {
                p++;
                v++;
            }
            else if (lastStar >= 0)
            {
                // Mismatch after a star: let that star swallow one more character and try again.
                p = lastStar + 1;
                v = ++resumeAt;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool CharsMatch(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
