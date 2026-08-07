using Craft.PowerShellHost;
using Craft.Setup;

namespace Craft.Hosting;

/// <summary>
/// While the HTTP worker pool is still warming up, steers traffic to a loading page (or a 503 for
/// API callers) instead of letting requests hit an empty pool. Pass-through once the pool is ready,
/// when this node has no Http role, or for health/setup paths — see <see cref="StartupGate"/>.
/// </summary>
internal static class StartupGateMiddleware
{
    /// <summary>
    /// Registers the startup-loading gate. No-ops immediately when <paramref name="httpEnabled"/> is
    /// false or the pool is already ready (or absent).
    /// </summary>
    public static WebApplication UseCraftStartupGate(
        this WebApplication app,
        bool httpEnabled,
        PowerShellWorkerPool? pool,
        bool setupEnabled,
        bool healthEnabled,
        string healthPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(healthPath);

        app.Use(async (context, next) =>
        {
            if (!httpEnabled || pool is null || pool.IsReady)
            {
                await next();
                return;
            }

            switch (StartupGate.Decide(context.Request.Path.Value ?? "",
                                       setupEnabled, healthEnabled, healthPath))
            {
                case StartupGateAction.PassThrough:
                    await next();
                    return;

                case StartupGateAction.ApiUnavailable:
                    context.Response.StatusCode = 503;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":"Application is starting up. Please wait."}""");
                    return;

                default:
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(SetupPages.StartupHtml);
                    return;
            }
        });

        return app;
    }
}
