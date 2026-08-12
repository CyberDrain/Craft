using Craft.Orchestration;
using Craft.PowerShellHost;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Job and run status API. Served directly from C# against the in-memory job manager, deliberately
/// bypassing the PowerShell pool — these are polled by dashboards during a fan-out, exactly when the
/// worker pool is busiest and least able to spare a runspace.
/// </summary>
public static class JobEndpoints
{
    /// <summary>Maps the <c>/API/jobs/*</c> and <c>/API/runs/*</c> routes.</summary>
    public static WebApplication MapCraftJobEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var jobManager = app.Services.GetRequiredService<JobManager>();
        var orchestrator = app.Services.GetRequiredService<OrchestratorService>();
        var bgLimiter = app.Services.GetRequiredService<BackgroundTaskLimiter>();
        var pool = app.Services.GetRequiredService<PowerShellWorkerPool>();
        var queueStatus = app.Services.GetRequiredService<JobQueueStatusReader>();

        app.MapGet("/API/jobs/summary", async (HttpContext context, CancellationToken ct) =>
        {
            context.Response.ContentType = "application/json";
            return Results.Ok(await queueStatus.GetSummaryAsync(ct));
        });

        // Worker-allocation snapshot: JobManager queue/active, the concurrency limiter's live gate, and
        // the BG pool's busy/idle workers. Poll it during a fan-out to watch the ramp, worker
        // utilisation and I/O idle over time. Polled at 4 Hz by the perf harness, so the durable-queue
        // numbers come from the cached snapshot (GetCached never blocks on storage).
        app.MapGet("/API/jobs/allocation", () =>
        {
            var durable = queueStatus.GetCached();
            return Results.Json(new
            {
                jm = new
                {
                    active = jobManager.ActiveCount,
                    queued = jobManager.QueuedCount,
                    maxConcurrency = jobManager.MaxConcurrency,
                },
                queue = new
                {
                    unclaimed = durable?.Unclaimed ?? 0,
                    claimed = durable?.Claimed ?? 0,
                    total = durable?.Total ?? 0,
                    ageSeconds = durable?.AgeSeconds,
                },
                limiter = new
                {
                    currentMax = bgLimiter.CurrentMax,
                    effectiveMax = bgLimiter.EffectiveMax,
                    overSubscribe = bgLimiter.OverSubscribe,
                    burst = bgLimiter.BurstToCeiling,
                    active = bgLimiter.Active,
                    waiting = bgLimiter.Waiting,
                    httpThrottled = bgLimiter.IsHttpThrottled,
                },
                pool = new
                {
                    bgBusy = pool.BgPoolSize - pool.BgAvailable,
                    bgTotal = pool.BgPoolSize,
                    bgAvail = pool.BgAvailable,
                    httpAvail = pool.HttpAvailable,
                },
            });
        });

        app.MapGet("/API/jobs/runs", async (HttpContext context, CancellationToken ct) =>
        {
            context.Response.ContentType = "application/json";
            return Results.Ok(await queueStatus.GetRunSummariesAsync(ct));
        });

        app.MapGet("/API/jobs/list", async (HttpContext context, CancellationToken ct) =>
        {
            var runName = context.Request.Query["runName"].ToString();
            var status = context.Request.Query["status"].ToString();
            var limit = int.TryParse(context.Request.Query["limit"].ToString(), out var l) ? l : 100;

            var jobs = await queueStatus.GetJobDetailsAsync(
                string.IsNullOrEmpty(runName) ? null : runName,
                string.IsNullOrEmpty(status) ? null : status,
                limit, ct);

            context.Response.ContentType = "application/json";
            return Results.Ok(jobs);
        });

        app.MapPost("/API/runs/cancel", async (HttpContext context) =>
        {
            var name = context.Request.Query["name"].ToString();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "name parameter is required" });

            var (found, cancelledCount) = await orchestrator.CancelRunAsync(name);

            // Callers usually pass the bare orchestration name; the registered run is the cmdlet that
            // starts it, so retry with the conventional Start- prefix before giving up.
            if (!found) (found, cancelledCount) = await orchestrator.CancelRunAsync($"Start-{name}");

            if (!found) return Results.NotFound(new { error = $"No run found for '{name}'" });

            return Results.Ok(new
            {
                name,
                cancelled = cancelledCount,
                message = $"Cancelled {cancelledCount} pending tasks",
            });
        });

        return app;
    }
}
