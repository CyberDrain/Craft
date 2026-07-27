using Craft.Services;
using Craft.Setup;

namespace Craft.Hosting;

/// <summary>
/// Steers traffic to or away from the first-run setup wizard.
/// <para>
/// Setup mode is opt-in: the hosted app calls <c>AppLifecycleBridge.RequestSetupMode()</c> once it
/// determines it cannot configure itself from existing credentials. Until then requests flow normally.
/// The branch table lives in <see cref="SetupGate"/>; this only performs the chosen action.
/// </para>
/// </summary>
public static class SetupModeMiddleware
{
    /// <summary>Registers the setup gate when <c>App:Setup:Enabled</c>.</summary>
    public static WebApplication UseCraftSetupGate(this WebApplication app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.Use(async (context, next) =>
        {
            var action = SetupGate.Decide(
                context.Request.Path.Value ?? "",
                SetupService.IsEasyAuthConfigured(),
                AppLifecycleBridge.IsSetupModeRequested());

            switch (action)
            {
                case SetupGateAction.PassThrough:
                    await next();
                    return;

                case SetupGateAction.SetupCompleteStatus:
                    // 200 with a body the restart-screen poller can act on, plus a Location header for
                    // any client that treats this as a redirect.
                    context.Response.StatusCode = 200;
                    context.Response.Headers["Location"] = "/";
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"isEasyAuthConfigured":true,"isSetupCompleted":false,"redirect":"/","message":"Setup is complete."}""");
                    return;

                case SetupGateAction.SetupEndpointsDisabled:
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":"Setup is complete. These endpoints are disabled."}""");
                    return;

                case SetupGateAction.ApiUnavailable:
                    context.Response.StatusCode = 503;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":"Setup required. Navigate to /setup to configure authentication."}""");
                    return;

                case SetupGateAction.RedirectToRoot:
                    context.Response.Redirect("/");
                    return;

                case SetupGateAction.RedirectToSetup:
                    context.Response.Redirect("/setup");
                    return;

                default:
                    await next();
                    return;
            }
        });

        logger.LogInformation("[Setup] Setup mode enabled — {Status}",
            SetupService.IsEasyAuthConfigured()
                ? "EasyAuth already configured, setup endpoints disabled"
                : "waiting for child app to call RequestSetupMode()");

        return app;
    }
}
