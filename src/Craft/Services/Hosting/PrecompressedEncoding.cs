namespace Craft.Hosting;

/// <summary>
/// Selection of a precompressed on-disk variant (<c>.br</c> / <c>.gz</c>) for a request.
/// <para>
/// Serving a sibling file that was compressed at build time costs zero per-request CPU, which matters
/// on a small container where on-the-fly Brotli competes with the PowerShell worker pool for the same
/// two cores. It also keeps a fixed <c>Content-Length</c> instead of switching the response to chunked.
/// </para>
/// </summary>
public readonly record struct PrecompressedEncoding(string ContentEncoding, string FileSuffix)
{
    /// <summary>Brotli — preferred, since it is smaller than gzip at equivalent effort.</summary>
    public static PrecompressedEncoding Brotli => new("br", ".br");

    /// <summary>gzip — the universally supported fallback.</summary>
    public static PrecompressedEncoding Gzip => new("gzip", ".gz");

    /// <summary>
    /// File extensions worth serving precompressed. Anything already compressed (images, fonts,
    /// archives) is excluded — recompressing those costs CPU and produces a larger file.
    /// </summary>
    public static IReadOnlySet<string> CompressibleExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".js", ".css", ".html", ".json", ".svg", ".xml", ".txt", ".map", ".wasm",
        };

    /// <summary>Whether <paramref name="path"/> has an extension worth a precompressed lookup.</summary>
    public static bool IsCompressiblePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var extension = Path.GetExtension(path);
        return extension.Length != 0 && CompressibleExtensions.Contains(extension);
    }

    /// <summary>
    /// Picks an encoding from an <c>Accept-Encoding</c> header value, preferring Brotli, or
    /// <see langword="null"/> when the client accepts neither.
    /// </summary>
    /// <remarks>
    /// Parses per RFC 9110 §12.5.3 rather than substring-matching: each comma-separated coding may
    /// carry a <c>;q=</c> weight, and <c>q=0</c> means "explicitly not acceptable". Brotli wins ties
    /// because it is smaller at equivalent effort, but an explicitly higher-weighted gzip is honoured.
    /// </remarks>
    public static PrecompressedEncoding? Negotiate(string? acceptEncoding)
    {
        if (string.IsNullOrWhiteSpace(acceptEncoding)) return null;

        double brotliWeight = -1, gzipWeight = -1;

        foreach (var range in acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = range.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var coding = parts[0].Trim();

            var isBrotli = coding.Equals("br", StringComparison.OrdinalIgnoreCase);
            var isGzip = coding.Equals("gzip", StringComparison.OrdinalIgnoreCase);
            if (!isBrotli && !isGzip) continue;

            var weight = ParseQuality(parts);
            if (isBrotli) brotliWeight = Math.Max(brotliWeight, weight);
            else gzipWeight = Math.Max(gzipWeight, weight);
        }

        // q=0 is an explicit refusal, not a low preference — treat it as "not offered".
        if (brotliWeight <= 0 && gzipWeight <= 0) return null;

        return brotliWeight >= gzipWeight ? Brotli : Gzip;
    }

    /// <summary>
    /// Reads the <c>;q=</c> weight from a parsed content-coding's parameters. Defaults to 1.0 when
    /// absent, and treats an unparseable weight as absent rather than as a refusal.
    /// </summary>
    private static double ParseQuality(string[] parts)
    {
        for (var i = 1; i < parts.Length; i++)
        {
            var parameter = parts[i].Trim();
            if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)) continue;

            return double.TryParse(parameter.AsSpan(2), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var q)
                ? q
                : 1.0;
        }

        return 1.0;
    }
}
