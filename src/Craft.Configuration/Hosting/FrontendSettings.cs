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
    /// <para>
    /// <c>'self'</c> leads <c>default-src</c> so that the app's own origin is allowed regardless of
    /// scheme. Without it the policy is scheme-gated on <c>https:</c>, and every same-origin
    /// <c>fetch</c> is blocked the moment the app is reached over http — behind a TLS-terminating
    /// proxy, on a self-hosted box, or in local docker. That is not theoretical: it silently killed
    /// the setup wizard's own status call, whose failure left the whole page disabled. It is also not
    /// a loosening — <c>'self'</c> permits exactly one origin, while the <c>https:</c> already there
    /// permits every https host there is.
    /// </para>
    /// <para>
    /// <c>connect-src</c> is spelled out because it needs one source <c>default-src</c> should not
    /// have: <c>data:</c>. Emscripten <c>SINGLE_FILE</c> builds inline their wasm as a
    /// <c>data:application/octet-stream;base64,…</c> URL and then <c>fetch</c> it, which is a
    /// <c>connect-src</c> request; wasm-backed layout and parsing libraries are commonly shipped this
    /// way. The loader falls back to decoding the base64 itself, so a block only costs a failed
    /// request and a console error, but there is no reason to pay it. Listing it here rather than in
    /// <c>default-src</c> keeps <c>data:</c> out of <c>script-src</c>, where it is an XSS vector.
    /// Everything else mirrors <c>default-src</c>, so this is parity plus <c>data:</c>; note that
    /// <c>ws:</c>/<c>wss:</c> are absent from both, so cross-origin websockets stay blocked.
    /// </para>
    /// </summary>
    public string? ContentSecurityPolicy { get; set; } =
        "default-src 'self' https: blob: 'unsafe-eval' 'unsafe-inline'; connect-src 'self' https: blob: data:; object-src 'self' blob:; img-src 'self' blob: data: *";

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

    /// <summary>
    /// Resolved compression state. <c>CRAFT_COMPRESSION</c> wins when set; otherwise
    /// <see cref="Compression"/> applies.
    /// </summary>
    public bool IsCompressionEnabled => ResolveCompressionEnabled(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Same as <see cref="IsCompressionEnabled"/> but with an injectable environment lookup (for tests).
    /// </summary>
    public bool ResolveCompressionEnabled(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var v = env("CRAFT_COMPRESSION");
        if (string.IsNullOrWhiteSpace(v)) return Compression;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}
