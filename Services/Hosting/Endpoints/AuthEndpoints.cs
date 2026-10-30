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
public static class AuthEndpoints
{
    /// <summary>Maps <c>/.auth/me</c> and <c>/api/me</c>.</summary>
    /// <param name="meIsNative">
    /// A native endpoint answers /api/me (see <see cref="MeRoute"/>), so it is not mapped here: an
    /// application without PowerShell workers has nothing to run the PowerShell <c>me</c> function on.
    /// </param>
    public static WebApplication MapCraftAuthEndpoints(
        this WebApplication app, CraftSettings settings, ILogger logger, bool meIsNative = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

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

        if (meIsNative)
        {
            logger.LogInformation("[Auth] /api/me is answered by the native endpoint {Route}", MeRoute(settings));
            return app;
        }

        var psRunner = app.Services.GetRequiredService<PowerShellRunnerService>();
        app.MapGet("/api/me", async (HttpContext context) =>
        {
            var meFunction = MeRoute(settings);

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
                    await context.Response.Body.WriteAsync(result.BodyBytes);
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

    /// <summary>
    /// The endpoint behind /api/me: <see cref="AuthSettings.MeEndpointFunction"/>, or "me". A native
    /// endpoint on this route answers /api/me itself; otherwise the PowerShell function of that name does.
    /// </summary>
    public static string MeRoute(CraftSettings settings) =>
        string.IsNullOrEmpty(settings.Auth.MeEndpointFunction) ? "me" : settings.Auth.MeEndpointFunction;
}
