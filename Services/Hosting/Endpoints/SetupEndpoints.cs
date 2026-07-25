using System.Text.Json;
using Craft.Configuration;
using Craft.PowerShellHost;
using Craft.Services;
using Craft.Setup;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// First-run setup wizard API. Implemented directly in C# with no PowerShell involved — setup has to
/// work before the worker pool is ready, and on a host that is not yet authenticated.
/// </summary>
public static class SetupEndpoints
{
    /// <summary>
    /// Maps <c>/api/setup/health</c> (always) plus the wizard routes (only when
    /// <c>App:Setup:Enabled</c>). Health is unconditional because the startup loading page polls it
    /// for readiness, including on hosts where the wizard itself is switched off.
    /// </summary>
    public static WebApplication MapCraftSetupEndpoints(this WebApplication app, CraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);

        var pool = app.Services.GetRequiredService<PowerShellWorkerPool>();
        var setupService = app.Services.GetRequiredService<SetupService>();

        app.MapGet("/api/setup/health", () =>
        {
            var info = StartupInfoBridge.GetInfo();
            return Results.Json(new
            {
                status = "ok",
                ready = pool.IsReady,
                phase = info.Phase,
                startup = new
                {
                    readinessMode = info.ReadinessMode,
                    warmupMode = info.WarmupMode,
                    cpuCount = info.CpuCount,
                    httpPoolSize = info.HttpPoolSize,
                    bgPoolSize = info.BgPoolSize,
                    sharedModules = info.SharedModuleCount,
                    httpOnlyModules = info.HttpOnlyModuleCount,
                    bgOnlyModules = info.BgOnlyModuleCount,
                    warmupMs = info.WarmupMs,
                    baseWorkerMs = info.BaseWorkerMs,
                    baseFunctions = info.BaseFunctionCount,
                    httpReadyMs = info.HttpReadyMs,
                    httpFunctions = info.HttpFunctionCount,
                    httpPoolFullMs = info.HttpPoolFullMs,
                    bgReadyMs = info.BgReadyMs,
                    bgFunctions = info.BgFunctionCount,
                    fullyReadyMs = info.FullyReadyMs,
                },
            });
        });

        if (!settings.Setup.Enabled) return app;

        app.MapGet("/setup", (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            return Results.Content(SetupPages.IndexHtml, "text/html");
        });

        app.MapGet("/api/setup/status", async (HttpContext context) =>
            Results.Json(await setupService.GetStatus(context.RequestAborted)));

        app.MapPost("/api/setup/device-code", async () =>
            Results.Json(await setupService.StartDeviceCodeFlow()));

        app.MapPost("/api/setup/device-code-poll", async (HttpContext context) =>
        {
            var root = await ReadJsonBodyAsync(context);

            var deviceCode = root.GetProperty("deviceCode").GetString()!;
            var result = await setupService.PollDeviceCodeFlow(deviceCode);

            // null means the user hasn't finished the device-code flow yet — not an error.
            return result is null
                ? Results.Json(new { pending = true })
                : Results.Json(new { pending = false, accessToken = result.AccessToken, tenantId = result.TenantId });
        });

        app.MapPost("/api/setup/create-auth-app", async (HttpContext context) =>
        {
            var root = await ReadJsonBodyAsync(context);

            var accessToken = root.GetProperty("accessToken").GetString()!;
            var tenantId = root.GetProperty("tenantId").GetString()!;
            var redirectUri = root.GetProperty("redirectUri").GetString()!;
            var multiTenant = root.TryGetProperty("multiTenant", out var mt) && mt.GetBoolean();

            return Results.Json(
                await setupService.CreateAuthAppRegistration(accessToken, tenantId, redirectUri, multiTenant));
        });

        app.MapPost("/api/setup/configure", async (HttpContext context) =>
        {
            if (AppLifecycleBridge.IsSetupCompleted()) return SetupAlreadyCompleted();

            var root = await ReadJsonBodyAsync(context);

            var appId = root.GetProperty("appId").GetString()!;
            var clientSecret = root.GetProperty("clientSecret").GetString()!;
            var tenantId = root.GetProperty("tenantId").GetString()!;
            var multiTenant = root.TryGetProperty("multiTenant", out var mt) && mt.GetBoolean();

            await setupService.ConfigureAppServiceAuth(appId, clientSecret, tenantId, multiTenant);
            AppLifecycleBridge.MarkSetupCompleted("EasyAuth configured via automated setup");

            return Results.Json(new
            {
                success = true,
                message = "App Service auth configured. The app will restart to apply changes.",
            });
        });

        app.MapPost("/api/setup/manual", async (HttpContext context) =>
        {
            if (AppLifecycleBridge.IsSetupCompleted()) return SetupAlreadyCompleted();

            var root = await ReadJsonBodyAsync(context);

            var appId = root.GetProperty("appId").GetString()!;
            var clientSecret = root.GetProperty("clientSecret").GetString()!;
            var tenantId = root.GetProperty("tenantId").GetString()!;
            var multiTenant = root.TryGetProperty("multiTenant", out var mt) && mt.GetBoolean();

            await setupService.ConfigureManual(appId, clientSecret, tenantId, multiTenant);
            AppLifecycleBridge.MarkSetupCompleted("EasyAuth configured via manual setup");

            return Results.Json(new
            {
                success = true,
                message = "App Service auth configured. The app will restart to apply changes.",
            });
        });

        app.MapPost("/api/setup/seed-user", async (HttpContext context) =>
        {
            try
            {
                var root = await ReadJsonBodyAsync(context);
                var upn = root.GetProperty("upn").GetString()!;

                await setupService.SeedFirstUser(upn, context.RequestAborted);
                return Results.Json(new { success = true, message = $"Superadmin user {upn} added successfully." });
            }
            catch (Exception ex)
            {
                // The wizard renders this message directly, so surface the reason rather than a bare 500.
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 400);
            }
        });

        return app;
    }

    /// <summary>
    /// Setup is single-shot: once it has run, the host is pending a restart and a second submission
    /// would reconfigure auth underneath the pending change.
    /// </summary>
    private static IResult SetupAlreadyCompleted() => Results.Json(
        new { success = false, message = "Setup already completed. The app is pending restart." },
        statusCode: 409);

    private static async Task<JsonElement> ReadJsonBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(context.RequestAborted);

        // Cloned because the JsonDocument is disposed on return; the element must outlive it.
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
