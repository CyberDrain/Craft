using System.Diagnostics;
using Craft.Auth;
using Craft.Caching;
using Craft.Configuration;

namespace Craft.Hosting;

/// <summary>
/// Normalises the platform-injected principal into the Static Web Apps shape the hosted PowerShell
/// application consumes.
/// <para>
/// Three cases:
/// <list type="number">
///   <item><description>App Service EasyAuth principal (<c>claims</c>, no <c>userRoles</c>) — transformed, with roles looked up from the allowedUsers table.</description></item>
///   <item><description>Already SWA-format (<c>userRoles</c> present) — passed through untouched.</description></item>
///   <item><description>No principal header at all — a dev principal is injected, in Development only.</description></item>
/// </list>
/// </para>
/// <para>
/// Authentication itself belongs to the upstream EasyAuth platform. This middleware only translates
/// and authorises; it never issues or validates a token.
/// </para>
/// </summary>
internal static class CraftAuthMiddleware
{
    /// <summary>Registers the principal-normalising middleware.</summary>
    public static WebApplication UseCraftAuth(
        this WebApplication app, CraftSettings settings, AuthService authService, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(logger);

        var isDevelopment = app.Environment.IsDevelopment();

        app.Use(async (context, next) =>
        {
            var authStart = CacheProfiler.Enabled ? Stopwatch.GetTimestamp() : 0;
            var path = context.Request.Path.Value ?? "";

            if (EasyAuthPrincipal.ShouldSkipInjection(path))
            {
                await next();
                return;
            }

            var hasPrincipal =
                context.Request.Headers.TryGetValue("x-ms-client-principal", out var existingHeader) &&
                !string.IsNullOrEmpty(existingHeader.ToString());

            if (hasPrincipal)
            {
                // Returns false when the caller was rejected and a response has already been written.
                if (!await TryNormalisePrincipalAsync(context, authService, logger, existingHeader.ToString()))
                    return;
            }
            else if (isDevelopment)
            {
                // Local dev only: EasyAuth owns authentication in a real deployment, where a missing
                // header simply means anonymous.
                InjectDevPrincipal(context, settings, logger, path);
            }

            if (CacheProfiler.Enabled) CacheProfiler.RecordAuth(Stopwatch.GetTimestamp() - authStart);
            await next();
        });

        return app;
    }

    /// <returns><see langword="false"/> if the request was rejected and the response is already written.</returns>
    private static async Task<bool> TryNormalisePrincipalAsync(
        HttpContext context, AuthService authService, ILogger logger, string headerValue)
    {
        try
        {
            using var document = EasyAuthPrincipal.Decode(headerValue);
            var root = document.RootElement;

            // Already SWA-format — pass through. EasyAuth strips inbound principal headers upstream, so
            // anything reaching here came from the trusted front end.
            if (!EasyAuthPrincipal.NeedsTransform(root)) return true;

            var realIdp = EasyAuthPrincipal.ResolveIdentityProvider(
                context.Request.Headers["x-ms-client-principal-idp"].ToString(), root);

            var claims = EasyAuthPrincipal.ExtractClaims(root);

            if (claims.IsAppOnly && !string.IsNullOrEmpty(claims.AppId))
            {
                // Service principal. The idp header MUST stay "aad": the hosted app keys off it to treat
                // the caller as an API client and resolve its name from the ApiClients table. The real
                // provider goes in identityProvider for audit only.
                context.Request.Headers["x-ms-client-principal"] = EasyAuthPrincipal.Encode(new
                {
                    identityProvider = realIdp,
                    userId = claims.ObjectId ?? claims.AppId,
                    userDetails = claims.AppId,
                    userRoles = Array.Empty<string>(),
                });
                context.Request.Headers["x-ms-client-principal-idp"] = "aad";
                context.Request.Headers["x-ms-client-principal-name"] = claims.AppId;
                return true;
            }

            if (string.IsNullOrEmpty(claims.Upn)) return true;

            var roles = await authService.GetUserRoles(claims.Upn);
            if (roles is null)
            {
                // Authenticated by the platform but not authorised here. Strip the header so nothing
                // downstream can mistake this for a valid principal.
                context.Request.Headers.Remove("x-ms-client-principal");
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized: user not in allowedUsers table.");
                return false;
            }

            context.Request.Headers["x-ms-client-principal"] = EasyAuthPrincipal.Encode(new
            {
                identityProvider = realIdp,
                userId = claims.ObjectId ?? claims.Upn,
                userDetails = claims.Upn,
                userRoles = roles,
            });
            context.Request.Headers["x-ms-client-principal-idp"] = "azureStaticWebApps";
            context.Request.Headers["x-ms-client-principal-name"] = claims.Upn;

            return true;
        }
        catch (Exception ex)
        {
            // Unparseable header — pass through untouched rather than 500. Downstream sees whatever was
            // there, and an unrecognised principal is treated as anonymous.
            logger.LogWarning(ex, "[Auth] Failed to parse x-ms-client-principal header");
            return true;
        }
    }

    private static void InjectDevPrincipal(
        HttpContext context, CraftSettings settings, ILogger logger, string path)
    {
        logger.LogDebug("[Auth] Dev auth bypass: injecting dev principal for {Path}", path);

        context.Request.Headers["x-ms-client-principal"] = EasyAuthPrincipal.Encode(new
        {
            identityProvider = settings.Auth.DevIdentityProvider,
            userId = settings.Auth.DevUserId,
            userDetails = settings.Auth.DevUserDetails,
            userRoles = settings.Auth.DevRoles.ToArray(),
        });

        // The idp header is the user-type signal (see the app-only branch above); the configurable
        // DevIdentityProvider only populates the principal's own identityProvider field.
        context.Request.Headers["x-ms-client-principal-idp"] = "azureStaticWebApps";
        context.Request.Headers["x-ms-client-principal-name"] = settings.Auth.DevUserDetails;
    }
}
