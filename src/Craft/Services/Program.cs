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

// Roles from IConfiguration only (no full CraftSettings dual-bind). Options pipeline is the
// single CraftSettings source after Build.
var roles = CraftRoles.Resolve(builder.Configuration);

if (roles.None)
{
    Console.Error.WriteLine("[System] FATAL: no deployment roles enabled — set at least one of " +
        "CRAFT_SERVE_FRONTEND / CRAFT_SERVE_API / CRAFT_RUN_BACKGROUND (or App:Roles:*). " +
        "Roles are declared by enabling what you want; unset roles default off once any is set.");
    Environment.Exit(78); // EX_CONFIG
}

var capFrontend = roles.Frontend;
var capHttp = roles.Http;
var capBackground = roles.Background;
var runPowerShell = roles.RunsPowerShell;
var cacheEnabled = roles.ResponseCacheEnabled;
var healthEnabled = roles.HealthEnabled;
var healthPath = roles.HealthPath;
var compressionEnabled = roles.CompressionEnabled;

builder.Services.AddCraftSettings(builder.Configuration, roles);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CraftSettings>>().Value);

builder.ConfigureCraftKestrel();

var (configuredLogLevel, fileLoggerProvider) = builder.AddCraftLogging();

builder.Services.AddCraftResponseCompression();

// Discover native C# endpoints/tasks before Build so they can register into DI.
var endpointSettings = new EndpointSettings();
builder.Configuration.GetSection("App:Endpoints").Bind(endpointSettings);
var nativeCatalog = NativeEndpointRegistry.Discover(
    endpointSettings,
    Path.Combine(AppContext.BaseDirectory, "API"),
    LoggerFactory.Create(b => b.AddSimpleConsole()).CreateLogger("Craft.Endpoints"));

builder.Services.AddCraftServices(roles);
if (!nativeCatalog.IsEmpty)
    builder.Services.AddNativeEndpoints(nativeCatalog, builder.Configuration);
builder.Services.AddCraftRateLimiter();

var app = builder.Build();
app.SyncFileLoggingFromOptions(fileLoggerProvider);

var httpDiagLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HttpDiag");
var httpListener = new HttpDiagnosticListener(httpDiagLogger, slowThresholdMs: 1000);
app.Lifetime.ApplicationStopping.Register(() => httpListener.Dispose());

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var CraftSettings = app.Services.GetRequiredService<CraftSettings>();
var startupProgress = app.Services.GetRequiredService<StartupProgressService>();
StartupInfoBridge.Initialize(startupProgress);

var cache = app.Services.GetRequiredService<CacheService>();
CacheBridge.Initialize(cache);

if (app.Services.GetService<RealtimeService>() is { } realtime)
    RealtimeBridge.Initialize(realtime);

ScriptRepository? repo = null;
PowerShellWorkerPool? pool = null;
PowerShellRunnerService? psRunner = null;
SetupService? setupService = null;
AuthService? authService = null;
JobManager? jobManager = null;
OrchestratorService? orchestrator = null;

if (runPowerShell)
{
    repo = app.Services.GetRequiredService<ScriptRepository>();
    pool = app.Services.GetRequiredService<PowerShellWorkerPool>();
    psRunner = app.Services.GetRequiredService<PowerShellRunnerService>();
    setupService = app.Services.GetRequiredService<SetupService>();
    AppLifecycleBridge.Initialize(app.Lifetime, logger, setupService);

    jobManager = app.Services.GetRequiredService<JobManager>();
    orchestrator = app.Services.GetRequiredService<OrchestratorService>();
    var queueDispatch = app.Services.GetRequiredService<QueueDispatchService>();
    var workerMetrics = app.Services.GetRequiredService<WorkerMetricsService>();
    WorkerMetricsBridge.Initialize(workerMetrics);
    OrchestratorBridge.Initialize(orchestrator);
    QueueBridge.Initialize(queueDispatch);

    authService = app.Services.GetService<AuthService>();
    if (authService is not null)
        AuthBridge.Initialize(authService);
    QueueStatusBridge.Initialize(app.Services.GetRequiredService<QueueStatusService>());
    SchedulerBridge.Initialize(app.Services.GetRequiredService<SchedulerService>());
    StatsHistoryBridge.Initialize(app.Services.GetRequiredService<StatsHistoryService>());
}

var healthMonitor = app.Services.GetRequiredService<ContainerHealthMonitor>();
if (CraftSettings.ContainerHealth.MaxRestarts > 0)
{
    healthMonitor.RecordStartupAttempt();
    if (healthMonitor.ShouldBlockStartup)
    {
        logger.LogCritical("[Health] Startup blocked due to crash loop — waiting for Azure to provision a new worker");
        await Task.Delay(Timeout.Infinite);
    }
}

var endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

var readinessMode = CraftSettings.ReadinessMode?.Trim() ?? "Immediate";

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
startupProgress.SetReadinessMode(readinessMode);

logger.LogInformation("[System] Roles: Frontend={Frontend} Http={Http} Background={Background} | " +
    "ResponseCache={Cache} Compression={Compression}",
    capFrontend ? "on" : "off", capHttp ? "on" : "off", capBackground ? "on" : "off",
    cacheEnabled ? "on" : "off", compressionEnabled ? "on" : "off");

void RunInitialization()
{
    if (repo is null || pool is null || psRunner is null)
        throw new InvalidOperationException("PowerShell services are not registered for this role.");

    repo.LoadAll(Path.Combine(AppContext.BaseDirectory, "API"));

    var discovered = psRunner.DiscoverHttpEndpoints();
    foreach (var kvp in discovered)
        endpoints[kvp.Key] = kvp.Value;

    logger.LogInformation("[System] {AppName}: {Count} API endpoints discovered", CraftSettings.Name, endpoints.Count);
    logger.LogInformation("[System] Pool: HTTP={Http} BG={Bg} LogLevel={LogLevel}",
        CraftSettings.Worker.HttpPoolSize,
        CraftSettings.Worker.BgPoolSize,
        configuredLogLevel);

    // HttpPoolSize/BgPoolSize = 0 opts out of that PowerShell pool (fully-native HTTP or native
    // scheduled tasks). Initialize signals readiness immediately when no pool is enabled.
    var enableHttpPool = capHttp && CraftSettings.Worker.HttpPoolSize > 0;
    if (capHttp && !enableHttpPool)
        logger.LogInformation("[System] HTTP worker pool disabled (Worker:HttpPoolSize=0) — PowerShell HTTP endpoints are not hosted");

    var enableBgPool = capBackground && CraftSettings.Worker.BgPoolSize > 0;
    if (capBackground && !enableBgPool)
        logger.LogInformation("[System] BG worker pool disabled (Worker:BgPoolSize=0) — native scheduled tasks only");

    pool.Initialize(enableHttp: enableHttpPool, enableBg: enableBgPool);

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
    var initTask = Task.Run(() =>
    {
        try { RunInitialization(); }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[System] Initialization failed");
            throw;
        }
    });

    while (pool is null || !pool.IsReady)
    {
        var finished = await Task.WhenAny(initTask, Task.Delay(500)).ConfigureAwait(false);
        if (finished == initTask)
        {
            await initTask.ConfigureAwait(false);
            if (pool is null || !pool.IsReady)
                throw new InvalidOperationException(
                    "Initialization finished without signaling HTTP ready.");
            break;
        }
    }

    logger.LogInformation("[System] HTTP pool ready — starting Kestrel (BG init continues in background)");
}
else if (readinessMode.Equals("AllReady", StringComparison.OrdinalIgnoreCase))
{
    try { RunInitialization(); }
    catch (Exception ex) { logger.LogCritical(ex, "[System] Initialization failed"); }
    logger.LogInformation("[System] All pools ready — starting Kestrel");
}
else
{
    throw new InvalidOperationException(
        $"Unexpected ReadinessMode '{readinessMode}'. Expected Immediate, HttpReady, or AllReady.");
}

if (app.Environment.IsDevelopment())
{
    logger.LogWarning("[Auth] Running in Development mode \u2014 unauthenticated requests will receive dev principal with roles: {Roles}",
        string.Join(", ", CraftSettings.Auth.DevRoles));
}

if (compressionEnabled)
    app.UseResponseCompression();
logger.LogInformation("[System] Static compression: {State}", compressionEnabled ? "enabled (precompressed .br/.gz + on-the-fly fallback)" : "DISABLED (raw/identity)");

if (CraftSettings.Setup.Enabled && setupService is not null)
    app.UseCraftSetupGate(logger);

app.UseCraftStartupGate(capHttp, pool, CraftSettings.Setup.Enabled, healthEnabled, healthPath);

var devFrontendUrl = DevFrontendProxy.ResolveDevServerUrl(
    capFrontend, app.Environment.IsDevelopment(), Environment.GetEnvironmentVariable);

HttpClient? devProxyClient = devFrontendUrl is null
    ? null
    : app.UseCraftDevFrontendProxy(devFrontendUrl, logger);

app.UseCraftContentSecurityPolicy(CraftSettings);

var frontendPath = Path.Combine(AppContext.BaseDirectory, "Frontend");
var frontendFileProvider = capFrontend
    ? app.UseCraftStaticFiles(frontendPath, compressionEnabled, logger)
    : null;

if (capFrontend && frontendFileProvider is null)
    logger.LogWarning("[System] Frontend directory not found: {Path}", frontendPath);

var storageHealth = (capHttp || capBackground)
    ? app.Services.GetService<StorageHealthMonitor>()
    : null;
if (storageHealth != null) _ = storageHealth.RefreshAsync();

app.MapCraftHealthEndpoint(roles, storageHealth, logger);
app.MapCraftRealtimeEndpoint(roles, CraftSettings, logger);
app.MapCraftPrmEndpoint(CraftSettings, logger);

// Auth middleware must run before the rate limiter so authenticated principals are
// available for partitioning. Static files are already mapped above — that order is
// load-bearing: anonymous static GETs must not consume the authenticated client's budget.
if (capHttp)
{
    app.UseCraftPublicCorsPreflight(CraftSettings, logger);
    if (authService is not null)
        app.UseCraftAuth(CraftSettings, authService, logger);
}

if (CraftSettings.RateLimit.IsEnabled)
    app.UseRateLimiter();

var activeRequests = new RequestCounter();

if (capHttp)
{
    if (authService is not null)
        app.MapCraftAuthEndpoints(CraftSettings, logger);
    app.MapCraftSetupEndpoints(CraftSettings);
    app.MapCraftJobEndpoints();

    if (nativeCatalog.Endpoints.Count > 0)
    {
        var mappable = NativeEndpointRegistry.ResolveCollisions(
            nativeCatalog.Endpoints, endpoints.Keys, CraftSettings.Endpoints.OnCollision, logger);
        app.MapCraftNativeEndpoints(mappable, activeRequests, logger, CraftSettings.Endpoints);
    }

    app.MapCraftPowerShellDispatch(endpoints, activeRequests, logger);
}

app.MapCraftFrontendFallback(
    new FrontendFallbackOptions(frontendFileProvider, devProxyClient, compressionEnabled, frontendPath),
    logger);

app.Run();
