namespace Craft.Configuration;

/// <summary>
/// Policy for dynamic <c>/api</c> responses (the PowerShell / native dispatch pipeline), kept separate
/// from <see cref="FrontendSettings"/> so the API surface can be governed on its own terms rather than
/// riding on the static-frontend toggles.
/// </summary>
public class ApiSettings
{
    /// <summary>
    /// Whether the host compresses dynamic <c>/api</c> responses on the fly (Brotli preferred, gzip
    /// fallback, negotiated from the caller's <c>Accept-Encoding</c>). Default true.
    /// <para>
    /// Independent of <see cref="FrontendSettings.Compression"/>, which governs <i>static</i> assets:
    /// turning static compression off (for example because an upstream CDN already compresses the
    /// static bundle) does NOT turn this off, and vice versa. That separation is the whole point — a
    /// CDN in front of the origin typically does not re-compress API JSON, so the origin should keep
    /// doing it even when static compression is delegated to the edge.
    /// </para>
    /// <para>
    /// This is a server capability, not a mandate: a response is only compressed when the caller
    /// advertises an accepted encoding; a client that sends none is served identity. Overridable via
    /// the <c>CRAFT_API_COMPRESSION</c> environment variable (true/false), which wins over this setting.
    /// </para>
    /// </summary>
    public bool Compression { get; set; } = true;

    /// <summary>
    /// On-the-fly compression level for the response compressors (Brotli and gzip): one of
    /// <c>Fastest</c>, <c>Optimal</c>, <c>SmallestSize</c>, or <c>NoCompression</c> (a
    /// <see cref="System.IO.Compression.CompressionLevel"/> name). Default <c>Optimal</c>.
    /// <para>
    /// Optimal is the default because it is a near-free win over Fastest: measured on a 2-vCPU container
    /// (PerfJson payload), Brotli Optimal compressed ~8.4x versus Fastest's ~4.9x at the <i>same</i> CPU
    /// (~14%) and latency, and gzip likewise improved at the same cost. <c>SmallestSize</c> is
    /// deliberately NOT the default: Brotli's SmallestSize is quality 11, which on a small shared-core
    /// container pegged both cores (~180% CPU) and drove p95 into the tens of seconds — never use it for
    /// dynamic <c>/api</c>. Drop to <c>Fastest</c> (or <c>NoCompression</c>) on a very small SKU if the
    /// compressor is seen competing with the PowerShell worker pool. Applies to on-the-fly compression
    /// generally (dynamic <c>/api</c> and the static fallback for assets without a precompressed sibling;
    /// precompressed <c>.br</c>/<c>.gz</c> siblings are built ahead of time and unaffected). Overridable
    /// via <c>CRAFT_API_COMPRESSION_LEVEL</c>. An unrecognised value falls back to Fastest.
    /// </para>
    /// </summary>
    public string CompressionLevel { get; set; } = "Optimal";
}
