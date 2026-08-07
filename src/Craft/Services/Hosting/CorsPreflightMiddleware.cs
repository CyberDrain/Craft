using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Answers CORS preflight (OPTIONS + Access-Control-Request-Method) requests, but ONLY for paths
/// the deployment has declared public via <c>App:Setup:ExcludedPaths</c> — the same list EasyAuth
/// exempts from authentication. Everything else falls through untouched.
///
/// <para>
/// Why it exists: the PowerShell dispatcher deliberately maps only real verbs, so a browser
/// preflight to an <c>/api/*</c> route fell through to the frontend fallback and 404'd — which
/// browsers treat as "cross-origin denied". That breaks browser-based OAuth/MCP clients (e.g.
/// claude.ai performing RFC 7591 dynamic client registration against a public endpoint): the
/// preflight dies before the actual POST is ever sent.
/// </para>
///
/// <para>
/// Scope rationale: a preflight answer grants nothing by itself — it only tells the browser it may
/// SEND the real request, and the browser only exposes the response if the endpoint itself emits
/// Access-Control-Allow-Origin. Restricting the responder to the excluded-paths list keeps the
/// surface identical to what the deployment already declared anonymous: authenticated routes keep
/// failing preflight exactly as before.
/// </para>
/// </summary>
public static class CorsPreflightMiddleware
{
    /// <summary>Registers the preflight responder for the configured public (excluded) paths.</summary>
    public static WebApplication UseCraftPublicCorsPreflight(
        this WebApplication app, CraftSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        // Snapshot at startup — the list only changes via config/app-setting updates, which
        // restart the container.
        var publicPaths = settings.Setup.ExcludedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        if (publicPaths.Length == 0)
        {
            logger.LogInformation("[System] CORS preflight responder: no excluded paths configured — inactive");
            return app;
        }

        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsOptions(context.Request.Method)
                && context.Request.Headers.ContainsKey("Access-Control-Request-Method")
                && IsPublicPath(context.Request.Path.Value ?? "", publicPaths))
            {
                var response = context.Response;
                response.StatusCode = StatusCodes.Status204NoContent;
                response.Headers.AccessControlAllowOrigin = "*";
                response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, PATCH, OPTIONS";
                // Echo whatever the browser asked to send; "*" is only valid without credentials,
                // and preflights are always credential-less.
                var requestedHeaders = context.Request.Headers.AccessControlRequestHeaders;
                response.Headers.AccessControlAllowHeaders =
                    string.IsNullOrEmpty(requestedHeaders) ? "*" : (string?)requestedHeaders;
                response.Headers.AccessControlMaxAge = "86400";
                return;
            }

            await next();
        });

        logger.LogInformation(
            "[System] CORS preflight responder: answering OPTIONS for {Count} public (excluded) path pattern(s)",
            publicPaths.Length);
        return app;
    }

    /// <summary>
    /// Matches a request path against the excluded-path patterns using the same semantics EasyAuth
    /// applies: exact (case-insensitive) match, or prefix match when the pattern ends in "/*".
    /// </summary>
    internal static bool IsPublicPath(string path, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                var prefix = pattern[..^1]; // keep the trailing slash: "/api/setup/"
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
