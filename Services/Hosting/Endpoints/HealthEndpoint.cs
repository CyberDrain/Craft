using Craft.PowerShellHost;
using Craft.Storage;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Role-agnostic liveness/readiness probe. Mapped before any role-gated block so it exists on every
/// topology — a background-only worker still needs somewhere for the platform to probe.
/// </summary>
public static class HealthEndpoint
{
    /// <summary>
    /// Maps the probe at <see cref="CraftRoles.HealthPath"/> when enabled, logging either way so the
    /// effective configuration is visible in startup output.
    /// </summary>
    /// <param name="app"></param>
    /// <param name="roles"></param>
    /// <param name="storageHealth">
    /// Storage monitor, or <see langword="null"/> on a node that never touches the store (a
    /// frontend-only origin, which then needs no connection string).
    /// </param>
    /// <param name="logger"></param>
    public static WebApplication MapCraftHealthEndpoint(
        this WebApplication app, CraftRoles roles, StorageHealthMonitor? storageHealth, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(logger);

        if (!roles.HealthEnabled)
        {
            logger.LogInformation("[System] Health endpoint: disabled");
            return app;
        }

        var pool = app.Services.GetRequiredService<PowerShellWorkerPool>();

        // Always 200 while the process is up — this is liveness. Restarting a container that is merely
        // still warming up would turn a slow start into a crash loop. Readiness is reported in the body.
        app.MapGet(roles.HealthPath, () =>
        {
            var httpReady = !roles.Http || pool.IsReady;
            var bgReady = !roles.Background || pool.BackgroundReady;
            var storageReady = storageHealth is null || storageHealth.Snapshot();

            return Results.Json(new
            {
                status = (httpReady && bgReady && storageReady) ? "ready" : "starting",
                roles = new { frontend = roles.Frontend, http = roles.Http, background = roles.Background },
                ready = new { http = httpReady, background = bgReady, storage = storageReady },
            });
        });

        logger.LogInformation("[System] Health endpoint: {Path}", roles.HealthPath);
        return app;
    }
}
