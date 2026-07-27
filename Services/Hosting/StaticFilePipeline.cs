using Craft.Configuration;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Craft.Hosting;

/// <summary>
/// Static content serving for <c>Frontend/</c>: CSP, precompressed variants, and cache headers.
/// </summary>
public static class StaticFilePipeline
{
    /// <summary>
    /// Applies the configured Content-Security-Policy to every response.
    /// </summary>
    /// <remarks>
    /// EasyAuth sets no response headers, so this is the only layer that can add a CSP. Registered
    /// before the static-file middleware so asset responses carry it too, and written from
    /// <c>OnStarting</c> so it lands even on responses produced by short-circuiting middleware.
    /// An existing header is never overwritten — a downstream app may have set a tighter policy.
    /// </remarks>
    public static WebApplication UseCraftContentSecurityPolicy(
        this WebApplication app, CraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);

        var csp = settings.Frontend.ContentSecurityPolicy;
        if (string.IsNullOrEmpty(csp)) return app;

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                if (!headers.ContainsKey("Content-Security-Policy"))
                    headers["Content-Security-Policy"] = csp;
                return Task.CompletedTask;
            });

            await next();
        });

        return app;
    }

    /// <summary>
    /// Serves <c>Frontend/</c> when this node carries the Frontend role and the directory exists.
    /// </summary>
    /// <returns>
    /// The shared file provider, or <see langword="null"/> when nothing is served. The SPA fallback
    /// reuses it rather than constructing its own — <see cref="PhysicalFileProvider"/> allocates file
    /// watchers, so a per-request instance leaks handles.
    /// </returns>
    public static IFileProvider? UseCraftStaticFiles(
        this WebApplication app, string frontendPath, bool compressionEnabled, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(frontendPath);
        ArgumentNullException.ThrowIfNull(logger);

        if (!Directory.Exists(frontendPath)) return null;

        var fileProvider = new PhysicalFileProvider(frontendPath);

        app.UseCraftPrecompressedStatic(fileProvider, compressionEnabled);

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            ServeUnknownFileTypes = false,
            HttpsCompression = HttpsCompressionMode.Compress,
            OnPrepareResponse = ctx =>
            {
                var path = ctx.Context.Request.Path.Value ?? "";
                var headers = ctx.Context.Response.Headers;
                var directive = StaticCachePolicy.For(path);

                headers.CacheControl = directive.CacheControl;
                if (directive.IncludeETag)
                    headers.ETag = $"\"{ctx.File.LastModified.ToFileTime():x}-{ctx.File.Length:x}\"";
            },
        });

        logger.LogInformation("[System] Frontend: {Path}", frontendPath);
        return fileProvider;
    }

    /// <summary>
    /// Serves a build-time-compressed sibling (<c>.br</c>/<c>.gz</c>) when one exists and the client
    /// accepts it, at zero per-request compression cost.
    /// </summary>
    /// <remarks>
    /// Registered ahead of the static-file middleware so it short-circuits. ResponseCompression sits
    /// upstream and skips anything with <c>Content-Encoding</c> already set, so nothing is compressed
    /// twice.
    /// </remarks>
    private static void UseCraftPrecompressedStatic(
        this WebApplication app, PhysicalFileProvider fileProvider, bool compressionEnabled)
    {
        var contentTypes = new FileExtensionContentTypeProvider();

        app.Use(async (context, next) =>
        {
            // Compression off: fall through to the raw static middleware (identity).
            if (!compressionEnabled) { await next(); return; }

            var path = context.Request.Path.Value ?? "";
            if (!PrecompressedEncoding.IsCompressiblePath(path)) { await next(); return; }

            var negotiated = PrecompressedEncoding.Negotiate(
                context.Request.Headers.AcceptEncoding.ToString());
            if (negotiated is not { } encoding) { await next(); return; }

            var variant = fileProvider.GetFileInfo(path.TrimStart('/') + encoding.FileSuffix);
            if (!variant.Exists || variant.IsDirectory || variant.PhysicalPath is null)
            {
                await next();
                return;
            }

            if (!contentTypes.TryGetContentType(path, out var contentType))
                contentType = "application/octet-stream";

            var headers = context.Response.Headers;
            headers.ContentEncoding = encoding.ContentEncoding;
            headers.Vary = "Accept-Encoding";
            context.Response.ContentType = contentType;
            headers.ETag = $"\"{variant.LastModified.ToFileTime():x}-{variant.Length:x}\"";
            headers.CacheControl = StaticCachePolicy.ForPrecompressed(path);

            // Explicit length so the precompressed body goes out fixed-length rather than chunked.
            context.Response.ContentLength = variant.Length;
            await context.Response.SendFileAsync(variant.PhysicalPath, context.RequestAborted);
        });
    }
}
