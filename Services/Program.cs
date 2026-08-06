using Craft.Auth;
using Craft.Caching;
using Craft.Configuration;
using Craft.Endpoints;
using Craft.Hosting;
using Craft.Hosting.Endpoints;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Realtime;
using Craft.Services;
using Craft.Setup;
using Craft.Storage;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Bind App section to CraftSettings
builder.Services.Configure<CraftSettings>(builder.Configuration.GetSection("App"));
// Apply SkuProfiles override (host-tier pool sizing) before any consumer resolves the options
builder.Services.PostConfigure<CraftSettings>(s => SkuProfileSelector.Apply(s));
// Also register a singleton accessor for non-DI contexts
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CraftSettings>>().Value);

// Bind configuration directly for early access (avoid BuildServiceProvider warning)
var craftSettings = new CraftSettings();
builder.Configuration.GetSection("App").Bind(craftSettings);

// ── Deployment roles (capabilities) ────────────────────────────────────────────────────────────────
// Resolution lives in CraftRoles so it can be unit tested without mutating process environment.
// See Services/Hosting/CraftRoles.cs for the full rule.
var roles = CraftRoles.Resolve(craftSettings);

if (roles.None)
{
    Console.Error.WriteLine("[System] FATAL: no deployment roles enabled — set at least one of " +
        "CRAFT_SERVE_FRONTEND / CRAFT_SERVE_API / CRAFT_RUN_BACKGROUND (or App:Roles:*). " +
        "Roles are declared by enabling what you want; unset roles default off once any is set.");
    Environment.Exit(78); // EX_CONFIG
}

// ── Thread pool ────────────────────────────────────────────────────────────────────────────────────
// Must happen before anything schedules work, and after config binding so the pool sizes are known.
// The default floor scales with the worker pools, not just the core count: PowerShell blocks a
// thread for the whole of every outbound call, so a pool of N workers can park N threads, and a
// minimum below that leaves the CLR injecting threads at ~1/second before the pool can reach its own
// concurrency. See CraftHostBuilderExtensions.ResolveMinThreads.
var minThreads = CraftHostBuilderExtensions.ResolveMinThreads(craftSettings);
ThreadPool.SetMinThreads(minThreads, minThreads);
var capFrontend = roles.Frontend;
var capHttp = roles.Http;
var capBackground = roles.Background;
var runPowerShell = roles.RunsPowerShell;
var cacheEnabled = roles.ResponseCacheEnabled;
var healthEnabled = roles.HealthEnabled;
var healthPath = roles.HealthPath;
var compressionEnabled = roles.CompressionEnabled;

// Kestrel limits, logging sinks, compression, the service graph and the rate limiter all live in
// Services/Hosting/CraftHostBuilderExtensions.cs.
builder.ConfigureCraftKestrel(craftSettings);

// The resolved level is logged at startup and also gates PowerShell stream capture.
var configuredLogLevel = builder.AddCraftLogging();

builder.Services.AddCraftResponseCompression();
// ── Native C# endpoints and scheduled tasks ────────────────────────────────────────────────────────
// Discovered before the container is built so the endpoint/task types, the central handler and any
// application service module they ship can be registered into it. Costs nothing when no assemblies
// are configured.
var nativeCatalog = NativeEndpointRegistry.Discover(
    craftSettings.Endpoints,
    Path.Combine(AppContext.BaseDirectory, "API"),
    LoggerFactory.Create(b => b.AddSimpleConsole()).CreateLogger("Craft.Endpoints"));

builder.Services.AddCraftServices(roles);
if (!nativeCatalog.IsEmpty)
    builder.Services.AddNativeEndpoints(nativeCatalog, builder.Configuration);
builder.Services.AddCraftRateLimiter(craftSettings);

var app = builder.Build();

// NOTE: the rate limiter middleware is deliberately NOT registered here. It runs after the auth
// middleware further down so it can partition on the caller's identity — see the comment there.

// HTTP diagnostic listener — tracks DNS, TLS, socket connect, and HTTP request timing
// from ALL HttpClient instances (including those inside PowerShell's Invoke-RestMethod)
var httpDiagLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HttpDiag");
var httpListener = new HttpDiagnosticListener(httpDiagLogger, slowThresholdMs: 1000);
// Must keep reference alive — GC would collect it and stop events
app.Lifetime.ApplicationStopping.Register(() => httpListener.Dispose());

var repo = app.Services.GetRequiredService<ScriptRepository>();
var pool = app.Services.GetRequiredService<PowerShellWorkerPool>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var psRunner = app.Services.GetRequiredService<PowerShellRunnerService>();
var cache = app.Services.GetRequiredService<CacheService>();
var CraftSettings = app.Services.GetRequiredService<CraftSettings>();
var realtime = app.Services.GetRequiredService<RealtimeService>();
RealtimeBridge.Initialize(realtime);
var setupService = app.Services.GetRequiredService<SetupService>();

// AppLifecycleBridge MUST be initialized before pool.Initialize() — the PS warmup script
// runs inside pool init and calls bridge methods (IsEasyAuthConfigured, ReconcileAuthPolicy,
// RequestSetupMode). If the bridge's static state isn't populated, those calls silently
// return false because the null-conditional logger swallows the "called before Initialize"
// warning. Other bridges (Scheduler, Cache, StatsHistory) initialize later — they're only
// called from request handlers or post-warmup PS, not from warmup itself.
AppLifecycleBridge.Initialize(app.Lifetime, logger, setupService);

// --- Container health monitoring ---
// Track restart attempts on persistent storage (/home) to detect crash loops.
// If the same instance has crashed too many times, block Kestrel so Azure provisions a new worker.
var healthMonitor = app.Services.GetRequiredService<ContainerHealthMonitor>();
if (CraftSettings.ContainerHealth.MaxRestarts > 0)
{
    healthMonitor.RecordStartupAttempt();
    if (healthMonitor.ShouldBlockStartup)
    {
        // Block indefinitely — Azure's warmup probe will time out (WEBSITES_CONTAINER_START_TIME_LIMIT)
        // and the platform will eventually reallocate to a new worker instance.
        logger.LogCritical("[Health] Startup blocked due to crash loop — waiting for Azure to provision a new worker");
        await Task.Delay(Timeout.Infinite);
    }
}

// Endpoints dictionary populated asynchronously after Kestrel starts.
// The startup middleware blocks all /API/* calls until pool.IsReady,
// so the route handler won't access this until it's fully populated.
var endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

// Readiness mode determines when Kestrel starts accepting connections:
// - Immediate: Kestrel starts first, init runs in background (loading page responds to Azure probes)
// - HttpReady: init runs before Kestrel, Kestrel starts once HTTP pool has a worker
// - AllReady:  init runs before Kestrel, Kestrel starts once all pools are fully initialized
var readinessMode = CraftSettings.ReadinessMode?.Trim() ?? "Immediate";

// Safety: on B1 (single vCPU, slow-tier CPU), init can take 150-200s+ — dangerously
// close to Azure's 230s container startup timeout. Auto-downgrade to Immediate to avoid kills.
// We check both CPU count and WEBSITE_SKU because premium single-vCPU plans (e.g. P0v3)
// are fast enough to init within the timeout despite having only 1 core.
var websiteSku = Environment.GetEnvironmentVariable("WEBSITE_SKU") ?? "";
var isSlowSingleCore = Environment.ProcessorCount <= 1
    && websiteSku.StartsWith("Basic", StringComparison.OrdinalIgnoreCase);

if (!readinessMode.Equals("Immediate", StringComparison.OrdinalIgnoreCase) && isSlowSingleCore)
{
    logger.LogWarning("[System] ReadinessMode '{Mode}' overridden to 'Immediate' — single vCPU on Basic SKU, " +
        "blocking Kestrel during init risks hitting Azure's 230s startup timeout", readinessMode);
    readinessMode = "Immediate";
}

logger.LogInformation("[System] Readiness mode: {Mode}", readinessMode);
StartupInfoBridge.SetReadinessMode(readinessMode);

// Announce the resolved deployment roles + derived toggles for this process.
logger.LogInformation("[System] Roles: Frontend={Frontend} Http={Http} Background={Background} | " +
    "ResponseCache={Cache} Compression={Compression}",
    capFrontend ? "on" : "off", capHttp ? "on" : "off", capBackground ? "on" : "off",
    cacheEnabled ? "on" : "off", compressionEnabled ? "on" : "off");

void RunInitialization()
{
    // 1. Load scripts — parse .ps1 files, build route table
    repo.LoadAll(Path.Combine(AppContext.BaseDirectory, "API"));

    // 2. Discover HTTP endpoints from loaded scripts
    var discovered = psRunner.DiscoverHttpEndpoints();
    foreach (var kvp in discovered)
        endpoints[kvp.Key] = kvp.Value;

    logger.LogInformation("[System] {AppName}: {Count} API endpoints discovered", CraftSettings.Name, endpoints.Count);
    logger.LogInformation("[System] Pool: HTTP={Http} BG={Bg} MinThreads={MinThreads} LogLevel={LogLevel}",
        CraftSettings.Worker.HttpPoolSize,
        CraftSettings.Worker.BgPoolSize,
        minThreads,
        configuredLogLevel);

    // 3. Initialize PowerShell worker pool (loads modules, creates runspaces).
    //    Build only the pools this node's roles require: Http → HTTP pool, Background → BG pool.
    // HttpPoolSize = 0 means this node hosts no PowerShell HTTP endpoints at all — a fully-native
    // app. Skipping the pool then is not just a saving (runspace construction, ~1.6 MiB each, plus
    // the PowerShell SDK's native allocations); it is the difference between paying for a
    // PowerShell host and not having one. Initialize() signals readiness immediately when no pool is
    // enabled, so the startup gate below does not block /API/* forever waiting for a pool that will
    // never exist.
    var enableHttpPool = capHttp && CraftSettings.Worker.HttpPoolSize > 0;
    if (capHttp && !enableHttpPool)
        logger.LogInformation("[System] HTTP worker pool disabled (Worker:HttpPoolSize=0) — PowerShell HTTP endpoints are not hosted");

    // BgPoolSize = 0 is the same opt-out for the background side: the app's scheduled work is all
    // native tasks, which run on the .NET thread pool, so BG runspaces would never be checked out.
    // The disabled pool signals its ready event immediately, so the scheduler still starts.
    var enableBgPool = capBackground && CraftSettings.Worker.BgPoolSize > 0;
    if (capBackground && !enableBgPool)
        logger.LogInformation("[System] BG worker pool disabled (Worker:BgPoolSize=0) — native scheduled tasks only");

    pool.Initialize(enableHttp: enableHttpPool, enableBg: enableBgPool);

    // Pool is ready — clear the restart counter so we don't carry stale crash state
    healthMonitor.ClearRestartCounter();
}

if (!runPowerShell)
{
    logger.LogWarning("[System] STATIC-ONLY (Frontend role) — PowerShell worker pool, scheduler, job manager " +
        "and background services are disabled. Serving static frontend content only; /api, /API and /.auth " +
        "return 404.");
}
else if (readinessMode.Equals("Immediate", StringComparison.OrdinalIgnoreCase))
{
    // Defer init until after Kestrel is listening — Azure probe gets a fast 200,
    // users see a loading page while workers initialize in the background.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Task.Run(() =>
        {
            try { RunInitialization(); }
            catch (Exception ex) { logger.LogCritical(ex, "[System] Initialization failed"); }
        });
    });
}
else if (readinessMode.Equals("HttpReady", StringComparison.OrdinalIgnoreCase))
{
    // Run init synchronously before Kestrel starts. pool.Initialize() signals _httpReady
    // after the first HTTP worker is in the pool, but Kestrel won't start until Initialize()
    // returns (which is after all pools are done). To start Kestrel at HTTP-ready, run init
    // on a background thread and wait only for HTTP readiness.
    var initTask = Task.Run(() =>
    {
        try { RunInitialization(); }
        catch (Exception ex) { logger.LogCritical(ex, "[System] Initialization failed"); }
    });
    // Block app.Run() until HTTP pool signals ready
    pool.WaitForReady(Timeout.InfiniteTimeSpan);
    logger.LogInformation("[System] HTTP pool ready — starting Kestrel (BG init continues in background)");
}
else if (readinessMode.Equals("AllReady", StringComparison.OrdinalIgnoreCase))
{
    // Run full init synchronously before Kestrel starts — container won't respond
    // to any requests until all workers (HTTP + BG) are initialized.
    try { RunInitialization(); }
    catch (Exception ex) { logger.LogCritical(ex, "[System] Initialization failed"); }
    logger.LogInformation("[System] All pools ready — starting Kestrel");
}
else
{
    logger.LogWarning("[System] Unknown ReadinessMode '{Mode}', falling back to Immediate", readinessMode);
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Task.Run(() =>
        {
            try { RunInitialization(); }
            catch (Exception ex) { logger.LogCritical(ex, "[System] Initialization failed"); }
        });
    });
}

if (app.Environment.IsDevelopment())
{
    logger.LogWarning("[Auth] Running in Development mode \u2014 unauthenticated requests will receive dev principal with roles: {Roles}",
        string.Join(", ", CraftSettings.Auth.DevRoles));
}

// Response compression must be before static files. Skipped entirely when compression is disabled
// (App:Frontend:Compression=false / CRAFT_COMPRESSION=false) so everything is served raw/identity.
if (compressionEnabled)
    app.UseResponseCompression();
logger.LogInformation("[System] Static compression: {State}", compressionEnabled ? "enabled (precompressed .br/.gz + on-the-fly fallback)" : "DISABLED (raw/identity)");

// Nodes without the Http role do not short-circuit /api or auth paths: the HTTP endpoints simply aren't
// mapped (see the `if (capHttp)` blocks below), so those requests fall through to static file serving
// (a Frontend node can expose /api/me etc. from its own static dir) and finally to MapFallback (which
// 404s unmatched /api|/.auth). Nothing here intercepts them.

// Setup mode: steers traffic to or away from the first-run wizard. Opt-in — the hosted app calls
// AppLifecycleBridge.RequestSetupMode() when it cannot self-configure. Decision table lives in
// Services/Setup/SetupGate.cs.
if (CraftSettings.Setup.Enabled) app.UseCraftSetupGate(logger);

// Startup loading screen: while the HTTP worker pool initialises, serve a holding page to browsers
// and 503 to API callers. Only nodes with the Http role have a pool to wait on. Probes always pass.
// Decision table lives in Services/Hosting/StartupGate.cs.
app.Use(async (context, next) =>
{
    if (!capHttp || pool.IsReady)
    {
        await next();
        return;
    }

    switch (StartupGate.Decide(context.Request.Path.Value ?? "",
                               CraftSettings.Setup.Enabled, healthEnabled, healthPath))
    {
        case StartupGateAction.PassThrough:
            await next();
            return;

        case StartupGateAction.ApiUnavailable:
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":"Application is starting up. Please wait."}""");
            return;

        default:
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(SetupPages.StartupHtml);
            return;
    }
});

// Dev proxy: in Development, proxy frontend requests (including the Fast Refresh WebSocket) to
// `next dev` instead of serving precompiled files. See Services/Hosting/DevFrontendProxy.cs.
var devFrontendUrl = DevFrontendProxy.ResolveDevServerUrl(
    capFrontend, app.Environment.IsDevelopment(), Environment.GetEnvironmentVariable);

HttpClient? devProxyClient = devFrontendUrl is null
    ? null
    : app.UseCraftDevFrontendProxy(devFrontendUrl, logger);

// CSP on every response, then static serving from Frontend/ (precompressed variants + cache policy).
// Only nodes with the Frontend role serve static content. See Services/Hosting/StaticFilePipeline.cs.
app.UseCraftContentSecurityPolicy(CraftSettings);

var frontendPath = Path.Combine(AppContext.BaseDirectory, "Frontend");
var frontendFileProvider = capFrontend
    ? app.UseCraftStaticFiles(frontendPath, compressionEnabled, logger)
    : null;

// Frontend role but no directory: the node will 404 rather than serve anything, which is worth
// saying out loud — it usually means the app image forgot to COPY its build output.
if (capFrontend && frontendFileProvider is null)
    logger.LogWarning("[System] Frontend directory not found: {Path}", frontendPath);

// Auth service
var authService = app.Services.GetRequiredService<AuthService>();

// Storage readiness — only relevant to roles that use the store (http: allowedUsers; background:
// orchestrator). A frontend-only node never touches storage, so it is not resolved there (which also
// avoids requiring a connection string on a pure static origin).
var storageHealth = (capHttp || capBackground)
    ? app.Services.GetRequiredService<StorageHealthMonitor>()
    : null;
if (storageHealth != null) _ = storageHealth.RefreshAsync(); // prime the cache off the request path

// Health probe (role-agnostic — mapped before the HTTP-role block so it survives every topology)
// and the realtime SSE channel. See Services/Hosting/Endpoints/.
app.MapCraftHealthEndpoint(roles, storageHealth, logger);
app.MapCraftRealtimeEndpoint(roles, CraftSettings, logger);

// OAuth protected resource metadata (RFC 9728) for MCP/OAuth client discovery. Anonymous by design —
// the setup reconcile keeps the well-known path in EasyAuth's excludedPaths while App:Prm is enabled.
// See Services/Hosting/Endpoints/PrmEndpoint.cs.
app.MapCraftPrmEndpoint(CraftSettings, logger);

// ── HTTP-role endpoints + middleware ──────────────────────────────────────────────────────────────
// A node without the Http role maps NONE of these, so /api and auth paths fall through to static serving
// (a Frontend node can expose them from its own static dir) and finally to MapFallback (404 for /api|/.auth).
if (capHttp)
{

    // Normalise the EasyAuth principal into the SWA shape the hosted PS app expects, then map the two
    // auth-adjacent routes. See Services/Hosting/CraftAuthMiddleware.cs and Endpoints/AuthEndpoints.cs.
    app.UseCraftAuth(CraftSettings, authService, logger);
    app.MapCraftAuthEndpoints(CraftSettings, logger);

} // end HTTP-role block (auth middleware). Bridges below run for any PS role; the
  // setup/jobs/PS-dispatch routes are re-gated in a second `if (capHttp)` block further down.

// Rate limiter middleware — only added when the limiter is registered on the service collection.
//
// Position is load-bearing, do not hoist this back to the top of the pipeline:
//   * It must run AFTER UseCraftAuth. App-only callers (client-credentials API clients) arrive with
//     no usable x-ms-client-principal-name — the auth middleware derives it from the token's appid.
//     Limiting before that ran collapsed every API client behind a shared egress IP into one bucket.
//   * It must run AFTER static file serving. A cold frontend load pulls dozens of assets, and
//     counting those against the caller's budget could throttle a user for opening a page.
// Left outside the capHttp block on purpose: a frontend-only node has no auth middleware and no
// worker pool, but should still be protected, partitioned by origin address as before.
if (CraftSettings.RateLimit.IsEnabled)
    app.UseRateLimiter();

// Concurrent request tracking for diagnostics. A holder object, not an int: the dispatch endpoint
// is registered elsewhere and a lambda cannot capture a ref local.
var activeRequests = new RequestCounter();

// --- Backend Process API ---
var orchestrator = app.Services.GetRequiredService<OrchestratorService>();
OrchestratorBridge.Initialize(orchestrator);
AuthBridge.Initialize(authService);
var jobManager = app.Services.GetRequiredService<JobManager>();
QueueBridge.Initialize(psRunner, jobManager, CraftSettings.Orchestrator.QueueTaskFunction);
QueueStatusBridge.Initialize(jobManager, app.Services.GetRequiredService<OrchestratorService>());
WorkerMetricsBridge.Initialize(pool, app.Services.GetRequiredService<BackgroundTaskLimiter>(), jobManager,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Craft.Services.WorkerMetricsBridge"));
SchedulerBridge.Initialize(app.Services.GetRequiredService<SchedulerService>());
CacheBridge.Initialize(cache);
StatsHistoryBridge.Initialize(app.Services.GetRequiredService<StatsHistoryService>());
// AppLifecycleBridge.Initialize is at the TOP of the file — it must run before pool.Initialize()
// so the PS warmup script can use it (it would silently return false otherwise).

// ── HTTP-role endpoints (continued): Setup API + Job Status + PowerShell dispatch ──
if (capHttp)
{

    // Setup wizard API and job/run status API — both plain C#, no PowerShell involved.
    // See Services/Hosting/Endpoints/.
    app.MapCraftSetupEndpoints(CraftSettings);
    app.MapCraftJobEndpoints();

    // Native C# endpoints. Mapped before the PowerShell dispatcher, though ASP.NET route precedence
    // would put a literal segment ahead of /API/{endpoint} regardless — which is what lets an app
    // migrate one endpoint at a time with the PowerShell function still loaded as the rollback.
    if (nativeCatalog.Endpoints.Count > 0)
    {
        var mappable = NativeEndpointRegistry.ResolveCollisions(
            nativeCatalog.Endpoints, endpoints.Keys, CraftSettings.Endpoints.OnCollision, logger);
        app.MapCraftNativeEndpoints(mappable, activeRequests, logger, CraftSettings.Endpoints);
    }

    // Dispatch /API/{endpoint} to the discovered PowerShell function. Owns the response cache,
    // stale-while-revalidate, and post-response trigger handling.
    // See Services/Hosting/Endpoints/PowerShellDispatchEndpoint.cs.
    app.MapCraftPowerShellDispatch(endpoints, activeRequests, logger);

} // end HTTP-role block (setup / jobs / PowerShell dispatch)

// Terminal fallback: proxy to the Next.js dev server in Development, otherwise serve a prerendered
// {path}.html or index.html for SPA routing. See Services/Hosting/Endpoints/FrontendFallbackEndpoint.cs.
app.MapCraftFrontendFallback(
    new FrontendFallbackOptions(frontendFileProvider, devProxyClient, compressionEnabled, frontendPath),
    logger);

app.Run();
