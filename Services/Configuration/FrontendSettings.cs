namespace Craft.Configuration;

/// <summary>
/// Frontend response policy. EasyAuth handles anon redirects and auth at the platform layer;
/// this exists for response headers EasyAuth doesn't touch.
/// </summary>
public class FrontendSettings
{
    /// <summary>
    /// Content-Security-Policy header value applied to all responses. Mirrors what SWA's
    /// globalHeaders.content-security-policy did. Defaults to the CIPP-compatible policy so a CSP is
    /// emitted secure-by-default even if a deployment doesn't configure one; override via
    /// App:Frontend:ContentSecurityPolicy (the hosted app can supply a tighter policy). Set to ""
    /// to disable.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; } =
        "default-src https: blob: 'unsafe-eval' 'unsafe-inline'; object-src 'self' blob:; img-src 'self' blob: data: *";

    /// <summary>
    /// Whether the host compresses static responses. Default true.
    /// - true: serves precompressed .br/.gz sibling files when present, and on-the-fly Brotli/Gzip as a
    ///   fallback for anything without a sibling.
    /// - false: serves everything raw/identity (no precompressed files served, ResponseCompression off).
    /// Turn off when an upstream CDN (e.g. Cloudflare) already compresses, when the content does not
    /// benefit, or to A/B measure compressed vs raw serving. Overridable via the CRAFT_COMPRESSION
    /// environment variable (true/false), which takes precedence over this setting.
    /// </summary>
    public bool Compression { get; set; } = true;
}
