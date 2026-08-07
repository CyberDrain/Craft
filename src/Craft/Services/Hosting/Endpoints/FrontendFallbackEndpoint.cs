using Microsoft.Extensions.FileProviders;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Everything the SPA fallback needs, gathered once at startup rather than resolved per request.
/// </summary>
/// <param name="FileProvider">
/// Shared provider for <c>Frontend/</c>, or <see langword="null"/> on a node without the Frontend role.
/// Deliberately shared: <see cref="PhysicalFileProvider"/> allocates file watchers, so constructing one
/// per request leaks handles.
/// </param>
/// <param name="DevProxyClient">
/// Client pointed at the Next.js dev server, or <see langword="null"/> outside Development.
/// </param>
/// <param name="CompressionEnabled">Whether precompressed <c>.br</c>/<c>.gz</c> siblings may be served.</param>
/// <param name="FrontendPath">Physical path of <c>Frontend/</c>, used for the index.html fallback.</param>
public sealed record FrontendFallbackOptions(
    IFileProvider? FileProvider,
    HttpClient? DevProxyClient,
    bool CompressionEnabled,
    string FrontendPath);

/// <summary>
/// Terminal handler for requests that matched no route: proxies to the Next.js dev server in
/// Development, and otherwise serves a prerendered <c>{path}.html</c> or falls back to
/// <c>index.html</c> for client-side routing.
/// </summary>
internal static class FrontendFallbackEndpoint
{
    /// <summary>
    /// Whether a path may be answered with an HTML document.
    /// </summary>
    /// <remarks>
    /// API and auth paths must never SPA-fallback. Serving <c>index.html</c> for an unmatched
    /// <c>/api/...</c> turns a 404 into a soft 200 — which a caller parses as garbage JSON, and which
    /// an edge cache like Cloudflare will happily cache against the API URL.
    /// </remarks>
    public static bool IsFallbackEligible(string path) =>
        !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/.auth", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> should be probed for a prerendered <c>{path}.html</c> export.
    /// Extensionless, non-root paths only — a request for <c>/logo.png</c> is a missing asset, not a route.
    /// </summary>
    public static bool IsPrerenderedRouteCandidate(string path) =>
        !string.IsNullOrEmpty(path) && path != "/" && !Path.HasExtension(path);

    /// <summary>Maps the terminal fallback handler.</summary>
    public static WebApplication MapCraftFrontendFallback(
        this WebApplication app, FrontendFallbackOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapFallback(async (HttpContext context) =>
        {
            var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

            // No frontend on this node (Http/Background-only role, or Frontend/ absent) — 404 rather
            // than faulting on a missing index.html.
            if (options.FileProvider is null && options.DevProxyClient is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (!IsFallbackEligible(path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (options.DevProxyClient is not null &&
                await TryProxyToDevServerAsync(context, options.DevProxyClient, path, logger))
            {
                return;
            }

            if (options.FileProvider is not null && IsPrerenderedRouteCandidate(path))
            {
                var file = options.FileProvider.GetFileInfo((path + ".html").TrimStart('/'));
                if (file.Exists && !file.IsDirectory && file.PhysicalPath is not null)
                {
                    await ServeHtmlAsync(context, file.PhysicalPath, options.CompressionEnabled);
                    return;
                }
            }

            // SPA client-side routing.
            await ServeHtmlAsync(context,
                Path.Combine(options.FrontendPath, "index.html"), options.CompressionEnabled);
        });

        return app;
    }

    /// <returns><see langword="true"/> if the response was written by the dev server.</returns>
    private static async Task<bool> TryProxyToDevServerAsync(
        HttpContext context, HttpClient client, string path, ILogger logger)
    {
        try
        {
            using var proxyRequest = new HttpRequestMessage(
                HttpMethod.Get, path + context.Request.QueryString);

            foreach (var header in context.Request.Headers)
            {
                if (!header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                    proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            using var proxyResponse = await client.SendAsync(
                proxyRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

            context.Response.StatusCode = (int)proxyResponse.StatusCode;
            foreach (var header in proxyResponse.Content.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in proxyResponse.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();

            // ASP.NET does its own chunking; leaving the upstream value produces a malformed response.
            context.Response.Headers.Remove("transfer-encoding");

            await proxyResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            return true;
        }
        catch (HttpRequestException ex)
        {
            // Dev server not up yet (or restarting) — fall through to the static files on disk.
            logger.LogWarning(
                "[DevProxy] Next.js dev server not reachable: {Message}. Falling back to static files.",
                ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends an HTML document, preferring a build-time-compressed sibling when the client accepts it.
    /// </summary>
    /// <remarks>
    /// Serving the precompressed file keeps a fixed <c>Content-Length</c> and costs zero compression
    /// CPU. The alternative — letting ResponseCompression Brotli it per request — strips the length and
    /// chunks the response. ResponseCompression sees <c>Content-Encoding</c> already set and passes
    /// through, so nothing is compressed twice.
    /// </remarks>
    private static async Task ServeHtmlAsync(
        HttpContext context, string physicalHtmlPath, bool compressionEnabled)
    {
        var headers = context.Response.Headers;
        context.Response.ContentType = "text/html";
        headers.CacheControl = "no-cache, must-revalidate";

        var negotiated = PrecompressedEncoding.Negotiate(context.Request.Headers.AcceptEncoding.ToString());

        if (compressionEnabled && negotiated is { } encoding)
        {
            var variant = new FileInfo(physicalHtmlPath + encoding.FileSuffix);
            if (variant.Exists)
            {
                headers.ContentEncoding = encoding.ContentEncoding;
                headers.Vary = "Accept-Encoding";
                headers.ETag = $"\"{variant.LastWriteTimeUtc.ToFileTime():x}-{variant.Length:x}\"";
                context.Response.ContentLength = variant.Length;
                await context.Response.SendFileAsync(variant.FullName, context.RequestAborted);
                return;
            }
        }

        // No precompressed sibling (a sub-1KB page, or compression disabled) — send raw with an
        // explicit Content-Length so the identity response stays fixed-length rather than chunked.
        var raw = new FileInfo(physicalHtmlPath);
        if (raw.Exists)
        {
            headers.ETag = $"\"{raw.LastWriteTimeUtc.ToFileTime():x}-{raw.Length:x}\"";
            context.Response.ContentLength = raw.Length;
        }

        await context.Response.SendFileAsync(physicalHtmlPath, context.RequestAborted);
    }
}
