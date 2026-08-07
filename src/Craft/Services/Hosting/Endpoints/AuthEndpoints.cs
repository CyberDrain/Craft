using System.Collections;
using System.Text.Json;
using Craft.Configuration;
using Craft.Services;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// The two auth-adjacent routes CRAFT serves.
/// <para>
/// Login, logout and callback are handled entirely by the upstream App Service EasyAuth layer at the
/// platform edge — CRAFT maps none of them.
/// </para>
/// </summary>
internal static class AuthEndpoints
{
    /// <summary>Maps <c>/.auth/me</c> and <c>/api/me</c>.</summary>
    public static WebApplication MapCraftAuthEndpoints(
        this WebApplication app, CraftSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var psRunner = app.Services.GetRequiredService<PowerShellRunnerService>();
        var isDevelopment = app.Environment.IsDevelopment();

        // In production EasyAuth serves /.auth/me at the edge, so this handler is shadowed and only
        // ever runs locally — where it lets the SPA boot without a login.
        app.MapGet("/.auth/me", () => Results.Json(new
        {
            clientPrincipal = isDevelopment
                ? new
                {
                    identityProvider = settings.Auth.DevIdentityProvider,
                    userId = settings.Auth.DevUserId,
                    userDetails = settings.Auth.DevUserDetails,
                    userRoles = settings.Auth.DevRoles.ToArray(),
                }
                : null,
        }));

        app.MapGet("/api/me", async (HttpContext context) =>
        {
            var meFunction = string.IsNullOrEmpty(settings.Auth.MeEndpointFunction)
                ? "me"
                : settings.Auth.MeEndpointFunction;

            var request = await PowerShellRunnerService.SnapshotRequest(context);
            var parameters = (Hashtable)request["Params"]!;
            parameters["CIPPEndpoint"] = meFunction;

            // ALWAYS 200, even on failure. The SPA treats clientPrincipal:null as "not authenticated"
            // and ignores the status code — returning 4xx/5xx here sends it into a retry storm
            // (observed in the wild as 40+ requests in 6 seconds). Real errors are logged, not returned.
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";

            try
            {
                var result = await psRunner.ExecuteHttpEndpoint(meFunction, request);

                if (result.StatusCode is >= 200 and < 300)
                {
                    await context.Response.WriteAsync(result.Body);
                    return;
                }

                logger.LogWarning("[Auth] /api/me PS returned {Status}: {Body}",
                    result.StatusCode, result.Body);

                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    clientPrincipal = (object?)null,
                    permissions = Array.Empty<string>(),
                    message = "Access denied, are you logged in? Using the correct account? " +
                              "If you are, contact your administrator to get access.",
                }));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Auth] /api/me failed");
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    clientPrincipal = (object?)null,
                    permissions = Array.Empty<string>(),
                }));
            }
        });

        return app;
    }
}
