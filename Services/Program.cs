using System.Collections;
using System.Net.Http;
using Craft.Services;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

// Ensure .NET ThreadPool has enough threads for concurrent I/O.
// Default min = ProcessorCount (e.g. 2 on B2 VM), which causes starvation
// when multiple PowerShell tasks make concurrent HTTP calls.
ThreadPool.SetMinThreads(
    Math.Max(Environment.ProcessorCount * 4, 32),
    Math.Max(Environment.ProcessorCount * 4, 32));

var builder = WebApplication.CreateBuilder(args);

// Bind App section to CraftSettings
builder.Services.Configure<CraftSettings>(builder.Configuration.GetSection("App"));
// Apply SkuProfiles override (host-tier pool sizing) before any consumer resolves the options
builder.Services.PostConfigure<CraftSettings>(ApplySkuProfile);
// Also register a singleton accessor for non-DI contexts
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CraftSettings>>().Value);

// Bind configuration directly for early access (avoid BuildServiceProvider warning)
var craftSettings = new CraftSettings();
builder.Configuration.GetSection("App").Bind(craftSettings);

// Parse an env var as a tri-state bool: null when unset/blank, else true for "true"/"1" (case-insensitive).
static bool? EnvFlag(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(v)) return null;
    return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
}

// ── Deployment roles (capabilities) ────────────────────────────────────────────────────────────────
// One image, three independent switches selected by env flag or App:Roles config:
//   Frontend   — serve static web content (Frontend/)
//   Http       — serve /api + auth via the HTTP PowerShell pool
//   Background — scheduler / orchestrator / job-manager / stats via the BG PowerShell pool
// Resolution: if ANY role is explicitly set (CRAFT_SERVE_*/CRAFT_RUN_* env wins over App:Roles) → use exactly
// those (unset → off); else (nothing set) → all three on (the combined monolith).
var roleFrontend = EnvFlag("CRAFT_SERVE_FRONTEND") ?? craftSettings.Roles.Frontend;
var roleHttp = EnvFlag("CRAFT_SERVE_API") ?? craftSettings.Roles.Http;
var roleBackground = EnvFlag("CRAFT_RUN_BACKGROUND") ?? craftSettings.Roles.Background;
var anyRoleExplicit = roleFrontend.HasValue || roleHttp.HasValue || roleBackground.HasValue;

bool capFrontend, capHttp, capBackground;
if (anyRoleExplicit)
{
    capFrontend = roleFrontend ?? false;
    capHttp = roleHttp ?? false;
    capBackground = roleBackground ?? false;
}
else
{
    capFrontend = true; capHttp = true; capBackground = true;
}

if (!capFrontend && !capHttp && !capBackground)
{
    Console.Error.WriteLine("[System] FATAL: no deployment roles enabled — set at least one of " +
        "CRAFT_SERVE_FRONTEND / CRAFT_SERVE_API / CRAFT_RUN_BACKGROUND (or App:Roles:*). " +
        "Roles are declared by enabling what you want; unset roles default off once any is set.");
    Environment.Exit(78); // EX_CONFIG
}

// Whether the host runs any PowerShell at all (HTTP handlers and/or background workers).
var runPowerShell = capHttp || capBackground;

// Response cache: default on only when a node serves BOTH a browser UI and its API (combined / frontend+http);
// off for api-only, worker-only and static-only. App:Cache:Enabled / CRAFT_RESPONSE_CACHE override the default.
var cacheEnabled = (EnvFlag("CRAFT_RESPONSE_CACHE") ?? craftSettings.Cache.Enabled) ?? (capFrontend && capHttp);

// Health probe: role-agnostic liveness/readiness endpoint. Disable-able and relocatable so a downstream
// deployment can put it behind a specific probe URL or turn it off. Env overrides win over App:Health.
var healthEnabled = EnvFlag("CRAFT_HEALTH_ENABLED") ?? craftSettings.Health.Enabled;
var healthPathEnv = Environment.GetEnvironmentVariable("CRAFT_HEALTH_PATH");
var healthPath = !string.IsNullOrWhiteSpace(healthPathEnv) ? healthPathEnv.Trim() : craftSettings.Health.Path;
if (!healthPath.StartsWith('/')) healthPath = "/" + healthPath;

// Compression toggle: when false, the host serves all static content raw/identity — precompressed
// .br/.gz siblings are not served and on-the-fly ResponseCompression is not applied. Lets a downstream
// app turn compression off (e.g. an upstream CDN already compresses, or the content doesn't benefit) and
// lets the perf harness A/B compressed vs raw on the same image. Default true (from App:Frontend:Compression);
// the CRAFT_COMPRESSION environment variable (true/false) takes precedence when set.
var compressionEnabled = EnvFlag("CRAFT_COMPRESSION") ?? craftSettings.Frontend.Compression;

static void ApplySkuProfile(CraftSettings s)
{
    if (s.Worker.IgnoreSkuProfiles)
    {
        Console.WriteLine("[System] SkuProfile evaluation skipped: IgnoreSkuProfiles=true; " +
            $"using baseline HttpPoolSize={s.Worker.HttpPoolSize} BgPoolSize={s.Worker.BgPoolSize}");
        return;
    }

    if (s.Worker.SkuProfiles.Count == 0) return; // feature not configured — silent

    try
    {
        var cpu = Environment.ProcessorCount;

        foreach (var p in s.Worker.SkuProfiles)
        {
            bool skuMatch;
            string? skuValue = null;
            if (string.IsNullOrWhiteSpace(p.SkuEnv))
            {
                skuMatch = true;
            }
            else
            {
                skuValue = Environment.GetEnvironmentVariable(p.SkuEnv) ?? "";
                skuMatch = string.Equals(skuValue, p.Sku ?? "", StringComparison.OrdinalIgnoreCase);
            }

            var cpuMatch = p.Cpu is null or 0 || p.Cpu == cpu;

            if (skuMatch && cpuMatch)
            {
                Console.WriteLine($"[System] SkuProfile matched (SkuEnv='{p.SkuEnv}' Sku='{p.Sku}' Cpu={p.Cpu}) " +
                    $"for runtime ({p.SkuEnv}='{skuValue}' ProcessorCount={cpu}); " +
                    $"applying HttpPoolSize={p.HttpPoolSize} BgPoolSize={p.BgPoolSize}");
                s.Worker.HttpPoolSize = p.HttpPoolSize;
                s.Worker.BgPoolSize = p.BgPoolSize;
                return;
            }
        }

        // Walked the full list, nothing matched — keep baseline
        Console.WriteLine($"[System] No SkuProfile matched runtime (ProcessorCount={cpu}, " +
            $"checked {s.Worker.SkuProfiles.Count} profile(s)); " +
            $"using baseline HttpPoolSize={s.Worker.HttpPoolSize} BgPoolSize={s.Worker.BgPoolSize}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[System] SkuProfile detection failed ({ex.GetType().Name}: {ex.Message}); " +
            $"using baseline HttpPoolSize={s.Worker.HttpPoolSize} BgPoolSize={s.Worker.BgPoolSize}");
    }
}

// Configure Kestrel limits. Request timeout: explicit KestrelTimeoutSeconds wins; else derive from
// Worker.HttpTimeoutSeconds; else default to 600s (10 min). The DoS-relevant limits below (body size,
// connection cap, slow-loris data rates) are always applied, independent of the timeout.
var kestrelTimeout = craftSettings.KestrelTimeoutSeconds;
if (kestrelTimeout <= 0)
    kestrelTimeout = craftSettings.Worker.HttpTimeoutSeconds > 0
        ? craftSettings.Worker.HttpTimeoutSeconds
        : 600;

builder.WebHost.ConfigureKestrel(options =>
{
    // Request timeout settings
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(kestrelTimeout);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(Math.Min(60, kestrelTimeout));

    // HTTP/2 settings for better multiplexing
    options.Limits.Http2.MaxStreamsPerConnection = 100;
    options.Limits.Http2.HeaderTableSize = 4096;
    options.Limits.Http2.MaxFrameSize = 16384;
    options.Limits.Http2.MaxRequestHeaderFieldSize = 8192;
    options.Limits.Http2.InitialConnectionWindowSize = 131072;
    options.Limits.Http2.InitialStreamWindowSize = 98304;

    // Request body size cap. Default 100 MB; 0 = unlimited.
    var maxBodyMb = craftSettings.Limits.MaxRequestBodyMB;
    options.Limits.MaxRequestBodySize = maxBodyMb > 0 ? maxBodyMb * 1024L * 1024L : null;

    // Concurrent connection cap. Default 200; <= 0 = unlimited (let the OS handle).
    var maxConn = craftSettings.Limits.MaxConcurrentConnections;
    options.Limits.MaxConcurrentConnections = maxConn > 0 ? maxConn : null;
    options.Limits.MaxConcurrentUpgradedConnections = maxConn > 0 ? maxConn : null;

    // Prevent slow-loris attacks — minimum data rates
    options.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
        bytesPerSecond: 240,   // 240 bytes/sec minimum
        gracePeriod: TimeSpan.FromSeconds(5));
    options.Limits.MinResponseDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
        bytesPerSecond: 240,
        gracePeriod: TimeSpan.FromSeconds(5));
});

// Resolve the configured log level (supports CRAFT_LOG_LEVEL env var override)
var fileLoggingSettings = new FileLoggingSettings();
builder.Configuration.GetSection("App:FileLogging").Bind(fileLoggingSettings);
var configuredLogLevel = fileLoggingSettings.ParsedLogLevel;

// File logging with rotation
var fileLoggerProvider = new FileLoggerProvider(fileLoggingSettings, configuredLogLevel);
builder.Logging.AddProvider(fileLoggerProvider);
LogBridge.Initialize(fileLoggerProvider);

// Console: timestamps + respect configured level
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.SingleLine = true;
});
if (configuredLogLevel > LogLevel.Debug)
{
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
        level => level >= LogLevel.Information);
}

// Suppress noisy ASP.NET framework logging unless at Debug level or lower
if (configuredLogLevel > LogLevel.Debug)
{
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Warning);
}

// Response compression (matches Azure SWA behavior)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/json", "text/json", "application/javascript", "text/javascript" });
});

// Configure compression levels for better performance/size tradeoff
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

// Register services
builder.Services.AddSingleton<ICraftTableStore, AzureTableStore>();
builder.Services.AddSingleton<StorageHealthMonitor>();
builder.Services.AddSingleton<RealtimeService>();

builder.Services.AddSingleton<ScriptRepository>();
builder.Services.AddSingleton<PowerShellWorkerPool>();
builder.Services.AddSingleton<PowerShellRunnerService>();
builder.Services.AddSingleton<CacheService>(sp => new CacheService(
    sp.GetRequiredService<ILogger<CacheService>>(),
    sp.GetRequiredService<CraftSettings>(),
    cacheEnabled));
builder.Services.AddSingleton<BackgroundTaskLimiter>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<OrchestratorTableStore>();
builder.Services.AddSingleton<OrchestratorStatusWriter>();
builder.Services.AddSingleton<OrchestratorService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddSingleton<StatsHistoryService>();
// Background hosted services (job manager, scheduler, stats history) only run on nodes with the Background role.
if (capBackground)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<JobManager>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StatsHistoryService>());
}
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<CraftSettings>>().Value.ContainerHealth;
    var monitorLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ContainerHealthMonitor>();
    return new ContainerHealthMonitor(monitorLogger, settings);
});

// Per-client fixed-window rate limiter, partitioned by authenticated principal name (fallback:
// X-Forwarded-For / remote IP) so a single caller cannot exhaust the small HTTP worker pool.
// Enabled by default; turn off via App:RateLimit:Enabled=false.
if (craftSettings.RateLimit.IsEnabled)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var key = context.Request.Headers["x-ms-client-principal-name"].ToString();
            if (string.IsNullOrEmpty(key))
            {
                // Behind Azure App Service the socket peer is the platform load balancer, so the real
                // client is in X-Forwarded-For — use its first hop so anonymous callers aren't all
                // collapsed into a single shared partition.
                var xff = context.Request.Headers["X-Forwarded-For"].ToString();
                key = !string.IsNullOrEmpty(xff)
                    ? xff.Split(',')[0].Trim()
                    : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
            }
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, craftSettings.RateLimit.PermitPerWindow),
                    Window = TimeSpan.FromSeconds(Math.Max(1, craftSettings.RateLimit.WindowSeconds)),
                    QueueLimit = Math.Max(0, craftSettings.RateLimit.QueueLimit),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
                });
        });
    });
}

var app = builder.Build();

// Rate limiter middleware — only added when the limiter is registered above.
if (craftSettings.RateLimit.IsEnabled)
    app.UseRateLimiter();

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
    logger.LogInformation("[System] Pool: HTTP={Http} BG={Bg} LogLevel={LogLevel}",
        CraftSettings.Worker.HttpPoolSize,
        CraftSettings.Worker.BgPoolSize,
        configuredLogLevel);

    // 3. Initialize PowerShell worker pool (loads modules, creates runspaces).
    //    Build only the pools this node's roles require: Http → HTTP pool, Background → BG pool.
    pool.Initialize(enableHttp: capHttp, enableBg: capBackground);

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

// Setup mode middleware: when Setup.Enabled, register setup route guards.
// The child app must call AppLifecycleBridge.RequestSetupMode() to activate the
// setup wizard (e.g. after determining it cannot auto-configure from existing credentials).
// (setupService is already resolved at the top of the file, alongside AppLifecycleBridge.Initialize)

if (CraftSettings.Setup.Enabled)
{
    app.Use(async (context, next) =>
    {
        if (SetupService.IsEasyAuthConfigured())
        {
            // EasyAuth is configured — redirect setup pages to /, pass everything else through
            var reqPath = context.Request.Path.Value ?? "";
            if (reqPath.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase))
            {
                // Allow health check endpoint through for readiness polling
                if (reqPath.Equals("/api/setup/health", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }
                // The restart-screen poller hits /api/setup/status to learn when the
                // new container is online with EasyAuth active. Return a 200 payload
                // it can act on (isEasyAuthConfigured=true, isSetupCompleted=false)
                // plus a Location header so any client treating this as a signed
                // redirect can navigate to the app root.
                if (reqPath.Equals("/api/setup/status", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 200;
                    context.Response.Headers["Location"] = "/";
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        "{\"isEasyAuthConfigured\":true,\"isSetupCompleted\":false,\"redirect\":\"/\",\"message\":\"Setup is complete.\"}");
                    return;
                }
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Setup is complete. These endpoints are disabled.\"}");
                return;
            }
            else if (reqPath.Equals("/setup", StringComparison.OrdinalIgnoreCase) ||
                     reqPath.StartsWith("/setup/", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect("/");
                return;
            }
            await next();
            return;
        }

        // EasyAuth NOT configured — setup mode only active after child app calls RequestSetupMode().
        if (!AppLifecycleBridge.IsSetupModeRequested())
        {
            // Setup not yet requested by child app — let requests through normally
            // (the startup loading middleware will handle the "pool not ready" case)
            await next();
            return;
        }

        // Setup mode is active
        var path = context.Request.Path.Value ?? "";

        // Allow setup API and setup page through
        if (path.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/setup", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/setup/", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Allow dev proxy assets through (HMR, etc.)
        if (path.StartsWith("/_next/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/__nextjs", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Allow static assets with file extensions through
        if (Path.HasExtension(path) &&
            !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/API/", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Block all other API calls with 503
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/API/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Setup required. Navigate to /setup to configure authentication.\"}");
            return;
        }

        // Redirect everything else to /setup
        context.Response.Redirect("/setup");
    });

    logger.LogInformation("[Setup] Setup mode enabled — {Status}",
        SetupService.IsEasyAuthConfigured() ? "EasyAuth already configured, setup endpoints disabled"
            : "waiting for child app to call RequestSetupMode()");
}

// Startup loading screen: while the HTTP worker pool is initializing, serve a loading page
// for browser requests and 503 for API calls. Only applies to nodes with the Http role — a Frontend-only
// or Background-only node has no HTTP pool to wait on. Health endpoint stays available for polling.
app.Use(async (context, next) =>
{
    if (capHttp && !pool.IsReady)
    {
        var path = context.Request.Path.Value ?? "";

        // Always let the health endpoints through for polling
        if (path.Equals("/api/setup/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/.craft/", StringComparison.OrdinalIgnoreCase) ||
            (healthEnabled && path.Equals(healthPath, StringComparison.OrdinalIgnoreCase)))
        {
            await next();
            return;
        }

        // Setup endpoints pass through only when setup mode is enabled
        if (CraftSettings.Setup.Enabled &&
            (path.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/setup", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/setup/", StringComparison.OrdinalIgnoreCase)))
        {
            await next();
            return;
        }

        // API calls get 503
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/API/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Application is starting up. Please wait.\"}");
            return;
        }

        // Browser requests get the startup loading page
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(SetupPages.StartupHtml);
        return;
    }
    await next();
});

// Dev proxy: in Development mode, proxy frontend requests to `next dev` (hot-reload)
// instead of serving precompiled static files from Frontend/. Only when this node has the Frontend role.
var devFrontendUrl = (capFrontend && app.Environment.IsDevelopment())
    ? Environment.GetEnvironmentVariable("CRAFT_DEV_FRONTEND_URL") ?? "http://localhost:3000"
    : null;
HttpClient? devProxyClient = null;
if (devFrontendUrl != null)
{
    devProxyClient = new HttpClient { BaseAddress = new Uri(devFrontendUrl) };
    devProxyClient.Timeout = TimeSpan.FromSeconds(120); // longer timeout for slow Next.js dev builds
    logger.LogInformation("[System] Dev mode: proxying frontend to {Url}", devFrontendUrl);

    // Fast Refresh (HMR) in `next dev` runs over a WebSocket under /_next/* (Turbopack in Next 16, or
    // webpack). Enable WebSockets so the dev proxy can bridge that upgrade to the Next.js dev server —
    // without this, HTML/asset GETs proxy fine but the HMR socket never establishes, so edits don't
    // hot-reload until a manual/forced browser refresh. Dev-only; no effect on production static serving.
    app.UseWebSockets();
    var devBaseUri = new Uri(devFrontendUrl);

    // Pump WebSocket frames one direction between the browser and the Next.js dev server.
    static async Task PumpDevWsAsync(System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (from.State == System.Net.WebSockets.WebSocketState.Open && to.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await to.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, ct);
                    break;
                }
                await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (System.Net.WebSockets.WebSocketException) { }
    }

    // In dev mode, intercept frontend requests (/_next/*, static assets, etc.)
    // and proxy them to the Next.js dev server before static file middleware runs
    app.Use(async (context, next) =>
    {
        var reqPath = context.Request.Path.Value ?? "";

        // HMR WebSocket upgrade (Fast Refresh, under /_next/*) — bridge it to the dev server both ways.
        if (context.WebSockets.IsWebSocketRequest)
        {
            var wsScheme = devBaseUri.Scheme == "https" ? "wss" : "ws";
            var target = new Uri($"{wsScheme}://{devBaseUri.Authority}{reqPath}{context.Request.QueryString}");
            using var upstream = new System.Net.WebSockets.ClientWebSocket();
            foreach (var proto in context.WebSockets.WebSocketRequestedProtocols)
                upstream.Options.AddSubProtocol(proto);
            try { await upstream.ConnectAsync(target, context.RequestAborted); }
            catch { context.Response.StatusCode = 502; return; }
            using var client = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
            await Task.WhenAll(
                PumpDevWsAsync(client, upstream, context.RequestAborted),
                PumpDevWsAsync(upstream, client, context.RequestAborted));
            return;
        }

        // Proxy to Next.js: /_next/*, /__nextjs, and any non-API path with a file extension
        // (e.g. /version.json, /manifest.json, /favicon.ico) that doesn't exist in Frontend/
        var shouldProxy = reqPath.StartsWith("/_next/") || reqPath.StartsWith("/__nextjs")
            || (Path.HasExtension(reqPath)
                && !reqPath.StartsWith("/API/", StringComparison.OrdinalIgnoreCase)
                && !reqPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                && !reqPath.StartsWith("/.auth/", StringComparison.OrdinalIgnoreCase));
        if (shouldProxy)
        {
            try
            {
                var targetUrl = reqPath + context.Request.QueryString;
                using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                foreach (var header in context.Request.Headers)
                {
                    if (!header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                        proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                using var proxyResponse = await devProxyClient.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead);
                context.Response.StatusCode = (int)proxyResponse.StatusCode;
                foreach (var header in proxyResponse.Content.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                foreach (var header in proxyResponse.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                context.Response.Headers.Remove("transfer-encoding");
                await proxyResponse.Content.CopyToAsync(context.Response.Body);
                return;
            }
            catch (HttpRequestException)
            {
                // Next.js dev server not running — fall through to static files
            }
        }
        await next();
    });
}

// Content-Security-Policy — applied to all responses. EasyAuth doesn't set response
// headers, so this is the only layer that can add CSP. Registered before UseStaticFiles
// so asset responses get the header too.
if (!string.IsNullOrEmpty(CraftSettings.Frontend.ContentSecurityPolicy))
{
    var csp = CraftSettings.Frontend.ContentSecurityPolicy;
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            if (!headers.ContainsKey("Content-Security-Policy"))
                headers["Content-Security-Policy"] = csp;
            return Task.CompletedTask;
        });
        await next();
    });
}

// Serve static files from Frontend/ (production mode, or fallback if dev proxy is down).
// Only when this node has the Frontend role — a Http/Background-only node serves no static content.
var frontendPath = Path.Combine(AppContext.BaseDirectory, "Frontend");
IFileProvider? frontendFileProvider = null;
if (capFrontend && Directory.Exists(frontendPath))
{
    frontendFileProvider = new PhysicalFileProvider(frontendPath);

    // Pre-compressed static serving: when a request for a compressible asset has a sibling
    // .br/.gz on disk and the client accepts that encoding, serve the precompressed file with
    // the original Content-Type and ZERO per-request compression CPU. Registered before the
    // static-file middleware so it short-circuits; ResponseCompression sees Content-Encoding
    // already set and skips, so nothing is compressed twice.
    var precompressContentTypes = new FileExtensionContentTypeProvider();
    var precompressExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".css", ".html", ".json", ".svg", ".xml", ".txt", ".map", ".wasm"
    };
    app.Use(async (context, next) =>
    {
        // Compression disabled: skip precompressed serving — fall through to raw UseStaticFiles (identity).
        if (!compressionEnabled) { await next(); return; }
        var reqPath = context.Request.Path.Value ?? "";
        var ext = Path.GetExtension(reqPath);
        if (ext.Length == 0 || !precompressExtensions.Contains(ext)) { await next(); return; }

        var accept = context.Request.Headers.AcceptEncoding.ToString();
        string? enc = null, suffix = null;
        if (accept.Contains("br", StringComparison.OrdinalIgnoreCase)) { enc = "br"; suffix = ".br"; }
        else if (accept.Contains("gzip", StringComparison.OrdinalIgnoreCase)) { enc = "gzip"; suffix = ".gz"; }
        if (enc == null) { await next(); return; }

        var variant = frontendFileProvider!.GetFileInfo(reqPath.TrimStart('/') + suffix);
        if (!variant.Exists || variant.IsDirectory || variant.PhysicalPath == null) { await next(); return; }

        if (!precompressContentTypes.TryGetContentType(reqPath, out var contentType))
            contentType = "application/octet-stream";
        var h = context.Response.Headers;
        h.ContentEncoding = enc;
        h.Vary = "Accept-Encoding";
        context.Response.ContentType = contentType;
        h.ETag = $"\"{variant.LastModified.ToFileTime():x}-{variant.Length:x}\"";
        h.CacheControl = reqPath.StartsWith("/_next/static/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=86400, immutable"
            : "no-cache, must-revalidate";
        // Set Content-Length explicitly so the precompressed body is sent fixed-length, not chunked
        // (ResponseCompression is upstream; with Content-Encoding already set it passes through).
        context.Response.ContentLength = variant.Length;
        await context.Response.SendFileAsync(variant.PhysicalPath);
    });

    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = frontendFileProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = frontendFileProvider,
        ServeUnknownFileTypes = false,
        HttpsCompression = Microsoft.AspNetCore.Http.Features.HttpsCompressionMode.Compress,
        OnPrepareResponse = ctx =>
        {
            var path = ctx.Context.Request.Path.Value ?? "";
            var headers = ctx.Context.Response.Headers;
            var etag = $"\"{ctx.File.LastModified.ToFileTime():x}-{ctx.File.Length:x}\"";

            // Never-cache control files: service worker, version probe, PWA manifest.
            if (path.EndsWith("/sw.js", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/version.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                headers.CacheControl = "no-cache, must-revalidate";
                headers.ETag = etag;
            }
            // Content-hashed bundles.
            else if (path.StartsWith("/_next/static/", StringComparison.OrdinalIgnoreCase))
            {
                headers.CacheControl = "public, max-age=86400, immutable";
            }
            // Stable-named binary assets (icons, report images, fonts) — long cache, revalidate on expiry.
            else if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") ||
                     path.EndsWith(".gif") || path.EndsWith(".ico") || path.EndsWith(".svg") ||
                     path.EndsWith(".webp") || path.EndsWith(".woff") || path.EndsWith(".woff2"))
            {
                headers.CacheControl = "public, max-age=86400, must-revalidate";
                headers.ETag = etag;
            }
            // Non-hashed data JSON (permissionsList, secureScore, languageList) — store + revalidate cheaply.
            else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                     !path.Contains("/api/", StringComparison.OrdinalIgnoreCase))
            {
                headers.CacheControl = "no-cache, must-revalidate";
                headers.ETag = etag;
            }
            // HTML and everything else — storable but always revalidated.
            else
            {
                headers.CacheControl = "no-cache, must-revalidate";
                headers.ETag = etag;
            }
        }
    });

    logger.LogInformation("[System] Frontend: {Path}", frontendPath);
}
else
{
    logger.LogWarning("[System] Frontend directory not found: {Path}", frontendPath);
}

// Auth service
var authService = app.Services.GetRequiredService<AuthService>();

// Storage readiness — only relevant to roles that use the store (http: allowedUsers; background:
// orchestrator). A frontend-only node never touches storage, so it is not resolved there (which also
// avoids requiring a connection string on a pure static origin).
var storageHealth = (capHttp || capBackground)
    ? app.Services.GetRequiredService<StorageHealthMonitor>()
    : null;
if (storageHealth != null) _ = storageHealth.RefreshAsync(); // prime the cache off the request path

// --- Health (role-agnostic; mapped before the HTTP-role block so it survives every role) ---
if (healthEnabled)
{
    // 200 whenever the process is up (liveness); the body's `ready` flags report per-role readiness.
    app.MapGet(healthPath, () =>
    {
        var httpReady = !capHttp || pool.IsReady;
        var bgReady = !capBackground || pool.BackgroundReady;
        var storageReady = storageHealth == null || storageHealth.Snapshot();
        return Results.Json(new
        {
            status = (httpReady && bgReady && storageReady) ? "ready" : "starting",
            roles = new { frontend = capFrontend, http = capHttp, background = capBackground },
            ready = new { http = httpReady, background = bgReady, storage = storageReady }
        });
    });
    logger.LogInformation("[System] Health endpoint: {Path}", healthPath);
}
else
{
    logger.LogInformation("[System] Health endpoint: disabled");
}

// ── Realtime SSE channel (/.craft/events) — served by http/frontend nodes ───────────────────────────
// Identity-gated delivery of job events published in-process via RealtimeBridge. See RealtimeService
// and docs/realtime-bridge-plan.md. Pure C# — never touches a PowerShell runspace.
// OPT-IN: off unless App:Realtime:Enabled=true (or CRAFT_REALTIME_ENABLED=true, which wins). When off the
// endpoint is never mapped, so /.craft/events falls through to the static/fallback handling like any
// unknown path, and RealtimeBridge publishes are dropped.
if ((capHttp || capFrontend) && CraftSettings.Realtime.IsEnabled)
{
    app.MapGet("/.craft/events", async (HttpContext ctx) =>
    {
        var userId = ctx.Request.Headers["x-ms-client-principal-name"].ToString();
        if (string.IsNullOrEmpty(userId)) { ctx.Response.StatusCode = 401; return; }

        var (connId, conn) = realtime.Connect(userId);
        if (conn == null) { ctx.Response.StatusCode = 503; return; } // over MaxConnections

        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // don't let nginx buffer the stream
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        var heartbeat = TimeSpan.FromSeconds(Math.Max(5, CraftSettings.Realtime.HeartbeatSeconds));
        var ct = ctx.RequestAborted;
        try
        {
            await ctx.Response.WriteAsync(": connected\n\n", ct);
            // Replay the current message for each of this user's live jobs so a (re)connect resyncs.
            foreach (var frame in realtime.CurrentFrames(userId))
                await ctx.Response.WriteAsync(frame, ct);
            await ctx.Response.Body.FlushAsync(ct);

            var reader = conn.Reader;
            while (!ct.IsCancellationRequested)
            {
                using var hb = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hb.CancelAfter(heartbeat);
                try
                {
                    if (await reader.WaitToReadAsync(hb.Token))
                    {
                        while (reader.TryRead(out var frame))
                            await ctx.Response.WriteAsync(frame, ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", ct); // heartbeat
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnected — normal */ }
        finally { realtime.Disconnect(userId, connId); }
    });
    logger.LogInformation("[System] Realtime SSE endpoint: /.craft/events");
}
else if (capHttp || capFrontend)
{
    logger.LogInformation("[System] Realtime SSE endpoint: disabled (set App:Realtime:Enabled=true to enable)");
}

// ── HTTP-role endpoints + middleware ──────────────────────────────────────────────────────────────
// A node without the Http role maps NONE of these, so /api and auth paths fall through to static serving
// (a Frontend node can expose them from its own static dir) and finally to MapFallback (404 for /api|/.auth).
if (capHttp)
{

// Auth middleware: normalizes the EasyAuth-injected principal for downstream PowerShell.
// 1. App Service EasyAuth (x-ms-client-principal has "claims", no "userRoles")
//    → transform to SWA format with roles looked up from the allowedUsers table
// 2. SWA-format principal (already has "userRoles") → pass through as-is
// 3. No principal header, Development only → inject a dev principal
app.Use(async (context, next) =>
{
    var authStart = CacheProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    var path = context.Request.Path.Value ?? "";

    // Skip header injection for static files and /.auth/*
    if (path.StartsWith("/_next/") || path.StartsWith("/assets/") ||
        path.StartsWith("/.auth/") || Path.HasExtension(path))
    {
        await next();
        return;
    }

    if (context.Request.Headers.TryGetValue("x-ms-client-principal", out var existingHeader) &&
        !string.IsNullOrEmpty(existingHeader.ToString()))
    {
        // x-ms-client-principal is present — check if it needs transformation
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(existingHeader.ToString()));
            using var doc = System.Text.Json.JsonDocument.Parse(decoded);
            var root = doc.RootElement;

            // Detect App Service EasyAuth format: has "claims" array but no "userRoles"
            if (root.TryGetProperty("claims", out _) && !root.TryGetProperty("userRoles", out _))
            {
                // The real identity provider EasyAuth resolved (aad, github, google, …). Source of
                // truth is the x-ms-client-principal-idp header EasyAuth injects; the decoded
                // principal's "auth_typ" carries the same value as a fallback. We surface this in
                // the emitted principal's identityProvider field for audit/display — but NOT on the
                // outgoing x-ms-client-principal-idp header, which CIPP overloads as a principal-TYPE
                // signal (aad = API client, azureStaticWebApps = interactive user). See below.
                var realIdp = context.Request.Headers["x-ms-client-principal-idp"].ToString();
                if (string.IsNullOrEmpty(realIdp) &&
                    root.TryGetProperty("auth_typ", out var authTypEl))
                    realIdp = authTypEl.GetString() ?? "";

                // Extract identity claims
                string? upn = null;
                string? oid = null;
                string? appId = null;
                string? idtyp = null;
                if (root.TryGetProperty("claims", out var claims))
                {
                    foreach (var claim in claims.EnumerateArray())
                    {
                        var typ = claim.GetProperty("typ").GetString() ?? "";
                        var val = claim.GetProperty("val").GetString() ?? "";
                        if (typ == "preferred_username" || typ == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")
                            upn ??= val;
                        else if (typ == "http://schemas.microsoft.com/identity/claims/objectidentifier")
                            oid ??= val;
                        else if (typ == "appid" || typ == "azp")
                            appId ??= val;
                        else if (typ == "idtyp")
                            idtyp ??= val;
                    }
                }

                // App-only (client-credentials) token: no UPN, has appid, or idtyp=="app"
                bool isAppOnly = string.IsNullOrEmpty(upn) &&
                                 (!string.IsNullOrEmpty(appId) || string.Equals(idtyp, "app", StringComparison.OrdinalIgnoreCase));

                if (isAppOnly && !string.IsNullOrEmpty(appId))
                {
                    // Service principal — emit SWA-format principal. The idp header MUST stay "aad"
                    // because CIPP keys off it to treat this as an API client and resolve AppName
                    // from the ApiClients table via x-ms-client-principal-name. The real provider
                    // (always aad for client-credentials) goes in identityProvider for audit.
                    var spFormat = new
                    {
                        identityProvider = realIdp,
                        userId = oid ?? appId,
                        userDetails = appId,
                        userRoles = Array.Empty<string>()
                    };
                    var spJson = System.Text.Json.JsonSerializer.Serialize(spFormat);
                    var spBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(spJson));
                    context.Request.Headers["x-ms-client-principal"] = spBase64;
                    context.Request.Headers["x-ms-client-principal-idp"] = "aad";
                    context.Request.Headers["x-ms-client-principal-name"] = appId;
                }
                else if (!string.IsNullOrEmpty(upn))
                {
                    // Look up user in allowedUsers table for CIPP roles
                    var roles = await authService.GetUserRoles(upn);
                    if (roles == null)
                    {
                        // User not authorized — strip the header so downstream sees anonymous
                        context.Request.Headers.Remove("x-ms-client-principal");
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized: user not in allowedUsers table.");
                        return;
                    }
                    // Interactive user — identityProvider carries the real provider (aad, github,
                    // google, …) for audit/display, mirroring SWA's clientPrincipal. The idp header
                    // MUST stay "azureStaticWebApps" so CIPP treats this as a user (reads userDetails)
                    // rather than misclassifying an interactive Entra login as an API client.
                    //
                    // INVARIANT (CIPP RBAC compat): this object must NOT include a "claims" array and
                    // MUST have a non-empty userDetails. CIPP's RBAC user branch reconstructs the
                    // principal — and overwrites userRoles with @('authenticated','anonymous') — when
                    // it sees claims present AND userDetails blank. We enter this branch only when upn
                    // is non-empty and emit no claims, so the real roles below survive. Keep it so.
                    var swaFormat = new
                    {
                        identityProvider = realIdp,
                        userId = oid ?? upn,
                        userDetails = upn,
                        userRoles = roles
                    };
                    var swaJson = System.Text.Json.JsonSerializer.Serialize(swaFormat);
                    var swaBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(swaJson));
                    context.Request.Headers["x-ms-client-principal"] = swaBase64;
                    context.Request.Headers["x-ms-client-principal-idp"] = "azureStaticWebApps";
                    context.Request.Headers["x-ms-client-principal-name"] = upn;
                }
            }
            // else: already in SWA format (has userRoles) — pass through as-is. EasyAuth strips
            // inbound principal headers upstream, so a header reaching us here came from the
            // trusted front end.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth] Failed to parse x-ms-client-principal header");
            // Pass through untouched if we can't parse it
        }

        if (CacheProfiler.Enabled) CacheProfiler.RecordAuth(System.Diagnostics.Stopwatch.GetTimestamp() - authStart);
        await next();
        return;
    }

    // No x-ms-client-principal header — local dev injects a dev principal (EasyAuth owns auth in real
    // deployments, so a missing header there simply means anonymous).
    if (app.Environment.IsDevelopment())
    {
        // Local dev: inject a dev principal so no login is required
        logger.LogDebug("[Auth] Dev auth bypass: injecting dev principal for {Path}", path);
        var devPrincipal = new
        {
            identityProvider = CraftSettings.Auth.DevIdentityProvider,
            userId = CraftSettings.Auth.DevUserId,
            userDetails = CraftSettings.Auth.DevUserDetails,
            userRoles = CraftSettings.Auth.DevRoles.ToArray()
        };
        var devJson = System.Text.Json.JsonSerializer.Serialize(devPrincipal);
        var devBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(devJson));
        context.Request.Headers["x-ms-client-principal"] = devBase64;
        // Keep the idp header as the user-type signal (see the EasyAuth branch above); the
        // configurable DevIdentityProvider only populates the principal's identityProvider field.
        context.Request.Headers["x-ms-client-principal-idp"] = "azureStaticWebApps";
        context.Request.Headers["x-ms-client-principal-name"] = CraftSettings.Auth.DevUserDetails;
    }

    if (CacheProfiler.Enabled) CacheProfiler.RecordAuth(System.Diagnostics.Stopwatch.GetTimestamp() - authStart);
    await next();
});

// --- Auth endpoints ---
// Login/logout/callback are handled by the upstream App Service EasyAuth layer at the platform edge;
// Craft maps none of them. In production EasyAuth also serves /.auth/me at the edge, so the handler
// below is shadowed — it only does anything in local development.

// /.auth/me — dev convenience: in Development, return the injected dev principal so the SPA boots
// without a login. In production EasyAuth serves this at the edge before it reaches here.
app.MapGet("/.auth/me", (HttpContext context) =>
{
    // Dev mode: return dev principal without requiring login
    if (app.Environment.IsDevelopment())
    {
        var devPrincipal = new
        {
            identityProvider = CraftSettings.Auth.DevIdentityProvider,
            userId = CraftSettings.Auth.DevUserId,
            userDetails = CraftSettings.Auth.DevUserDetails,
            userRoles = CraftSettings.Auth.DevRoles.ToArray()
        };
        return Results.Json(new { clientPrincipal = devPrincipal });
    }
    return Results.Json(new { clientPrincipal = (object?)null });
});

// /api/me — dispatch to Auth.MeEndpointFunction (or literal "me" if unset).
// MeEndpointHandler wrapping is resolved inside ExecuteHttpEndpoint.
//
// Always returns 200, even when PS errors. The SPA uses clientPrincipal:null as the
// "not authenticated" signal, not HTTP status — returning a 4xx/5xx here makes the
// frontend retry-storm (seen in the wild as 40+ requests in 6 seconds). When PS
// throws or returns non-2xx, we wrap with { clientPrincipal: null, permissions: [] }
// so the SPA boots cleanly into a login UI. Underlying PS errors are still logged
// at Warning level for diagnosis.
app.MapGet("/api/me", async (HttpContext context) =>
{
    var meFunction = string.IsNullOrEmpty(CraftSettings.Auth.MeEndpointFunction)
        ? "me"
        : CraftSettings.Auth.MeEndpointFunction;

    var request = await PowerShellRunnerService.SnapshotRequest(context);
    var parms = (System.Collections.Hashtable)request["Params"]!;
    parms["CIPPEndpoint"] = meFunction;

    context.Response.StatusCode = 200;
    context.Response.ContentType = "application/json";

    try
    {
        var result = await psRunner.ExecuteHttpEndpoint(meFunction, request);
        if (result.StatusCode is >= 200 and < 300)
        {
            await context.Response.WriteAsync(result.Body);
        }
        else
        {
            logger.LogWarning("[Auth] /api/me PS returned {Status}: {Body}", result.StatusCode, result.Body);
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                clientPrincipal = (object?)null,
                permissions = Array.Empty<string>(),
                message = "Access denied. Contact your administrator."
            }));
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Auth] /api/me failed");
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            clientPrincipal = (object?)null,
            permissions = Array.Empty<string>()
        }));
    }
});

} // end HTTP-role block (auth middleware). Bridges below run for any PS role; the
  // setup/jobs/PS-dispatch routes are re-gated in a second `if (capHttp)` block further down.

// Concurrent request tracking for diagnostics
var activeRequests = 0;

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

// --- Setup API (C# direct — no PS) ---

// Health endpoint always available — used by startup loading screen for readiness polling
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
            fullyReadyMs = info.FullyReadyMs
        }
    });
});

if (CraftSettings.Setup.Enabled)
{
app.MapGet("/setup", (HttpContext context) =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    return Results.Content(SetupPages.IndexHtml, "text/html");
});

app.MapGet("/api/setup/status", async (HttpContext context) =>
{
    var status = await setupService.GetStatus(context.RequestAborted);
    return Results.Json(status);
});

app.MapPost("/api/setup/device-code", async (HttpContext context) =>
{
    var result = await setupService.StartDeviceCodeFlow();
    return Results.Json(result);
});

app.MapPost("/api/setup/device-code-poll", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var root = doc.RootElement;

    var deviceCode = root.GetProperty("deviceCode").GetString()!;
    var result = await setupService.PollDeviceCodeFlow(deviceCode);

    if (result == null)
        return Results.Json(new { pending = true });

    return Results.Json(new { pending = false, accessToken = result.AccessToken, tenantId = result.TenantId });
});

app.MapPost("/api/setup/create-auth-app", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var root = doc.RootElement;

    var accessToken = root.GetProperty("accessToken").GetString()!;
    var tenantId = root.GetProperty("tenantId").GetString()!;
    var redirectUri = root.GetProperty("redirectUri").GetString()!;
    var multiTenant = root.TryGetProperty("multiTenant", out var mt) && mt.GetBoolean();

    var result = await setupService.CreateAuthAppRegistration(accessToken, tenantId, redirectUri, multiTenant);
    return Results.Json(result);
});

app.MapPost("/api/setup/configure", async (HttpContext context) =>
{
    if (AppLifecycleBridge.IsSetupCompleted())
        return Results.Json(new { success = false, message = "Setup already completed. The app is pending restart." }, statusCode: 409);

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var root = doc.RootElement;

    var appId = root.GetProperty("appId").GetString()!;
    var clientSecret = root.GetProperty("clientSecret").GetString()!;
    var tenantId = root.GetProperty("tenantId").GetString()!;
    var multiTenant = root.TryGetProperty("multiTenant", out var mt) && mt.GetBoolean();

    await setupService.ConfigureAppServiceAuth(appId, clientSecret, tenantId, multiTenant);
    AppLifecycleBridge.MarkSetupCompleted("EasyAuth configured via automated setup");
    return Results.Json(new { success = true, message = "App Service auth configured. The app will restart to apply changes." });
});

app.MapPost("/api/setup/manual", async (HttpContext context) =>
{
    if (AppLifecycleBridge.IsSetupCompleted())
        return Results.Json(new { success = false, message = "Setup already completed. The app is pending restart." }, statusCode: 409);

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var root = doc.RootElement;

    var appId = root.GetProperty("appId").GetString()!;
    var clientSecret = root.GetProperty("clientSecret").GetString()!;
    var tenantId = root.GetProperty("tenantId").GetString()!;
    var multiTenant = root.TryGetProperty("multiTenant", out var mt2) && mt2.GetBoolean();

    await setupService.ConfigureManual(appId, clientSecret, tenantId, multiTenant);
    AppLifecycleBridge.MarkSetupCompleted("EasyAuth configured via manual setup");
    return Results.Json(new { success = true, message = "App Service auth configured. The app will restart to apply changes." });
});

app.MapPost("/api/setup/seed-user", async (HttpContext context) =>
{
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        var upn = root.GetProperty("upn").GetString()!;
        await setupService.SeedFirstUser(upn, context.RequestAborted);
        return Results.Json(new { success = true, message = $"Superadmin user {upn} added successfully." });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = ex.Message }, statusCode: 400);
    }
});
} // end Setup.Enabled

// --- Job Status API (C# direct — no PS overhead) ---

app.MapGet("/API/jobs/summary", (HttpContext context) =>
{
    var summary = jobManager.GetSummary();
    context.Response.ContentType = "application/json";
    return Results.Ok(summary);
});

// Worker-allocation snapshot: JobManager queue/active, the concurrency limiter's live gate, and the BG pool's
// busy/idle workers. Poll it during a fan-out to see the ramp, worker utilization, and I/O idle over time.
var bgLimiter = app.Services.GetRequiredService<BackgroundTaskLimiter>();
app.MapGet("/API/jobs/allocation", () => Results.Json(new
{
    jm = new { active = jobManager.ActiveCount, queued = jobManager.QueuedCount, maxConcurrency = jobManager.MaxConcurrency },
    limiter = new { currentMax = bgLimiter.CurrentMax, effectiveMax = bgLimiter.EffectiveMax, overSubscribe = bgLimiter.OverSubscribe, burst = bgLimiter.BurstToCeiling, active = bgLimiter.Active, waiting = bgLimiter.Waiting, httpThrottled = bgLimiter.IsHttpThrottled },
    pool = new { bgBusy = pool.BgPoolSize - pool.BgAvailable, bgTotal = pool.BgPoolSize, bgAvail = pool.BgAvailable, httpAvail = pool.HttpAvailable }
}));

app.MapGet("/API/jobs/runs", (HttpContext context) =>
{
    var runs = jobManager.GetRunSummaries();
    context.Response.ContentType = "application/json";
    return Results.Ok(runs);
});

app.MapGet("/API/jobs/list", (HttpContext context) =>
{
    var runName = context.Request.Query["runName"].ToString();
    var status = context.Request.Query["status"].ToString();
    var limitStr = context.Request.Query["limit"].ToString();
    int? limit = int.TryParse(limitStr, out var l) ? l : null;

    var jobs = jobManager.GetJobs(
        string.IsNullOrEmpty(runName) ? null : runName,
        string.IsNullOrEmpty(status) ? null : status,
        limit);
    context.Response.ContentType = "application/json";
    return Results.Ok(jobs);
});

app.MapPost("/API/runs/cancel", async (HttpContext context) =>
{
    var name = context.Request.Query["name"].ToString();
    if (string.IsNullOrEmpty(name))
        return Results.BadRequest(new { error = "name parameter is required" });

    var (found, cancelledCount) = await orchestrator.CancelRunAsync(name);
    if (!found)
    {
        // Try with Start- prefix
        (found, cancelledCount) = await orchestrator.CancelRunAsync($"Start-{name}");
    }
    if (!found)
        return Results.NotFound(new { error = $"No run found for '{name}'" });

    return Results.Ok(new { name, cancelled = cancelledCount, message = $"Cancelled {cancelledCount} pending tasks" });
});

// Map all discovered PowerShell HTTP endpoints under /API/{EndpointName}
app.MapMethods("/API/{endpoint}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH" }, async (HttpContext context, string endpoint) =>
{
    var requestSw = System.Diagnostics.Stopwatch.StartNew();
    var concurrent = Interlocked.Increment(ref activeRequests);

    try
    {

        if (!endpoints.ContainsKey(endpoint))
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($"{{\"error\":\"Endpoint '{endpoint}' not found\"}}");
            return;
        }

        logger.LogInformation("[HTTP] {Method} /API/{Endpoint} start concurrent={Concurrent}",
            context.Request.Method, endpoint, concurrent);

        // Invalidate cache if requested via query parameter
        if (context.Request.Query.ContainsKey(cache.InvalidateParam)
            && string.Equals(context.Request.Query[cache.InvalidateParam], "true", StringComparison.OrdinalIgnoreCase))
        {
            // Scoped invalidation: if scope param is present, only invalidate matching entries
            var scopeParam = cache.ScopeParam;
            var scopeValue = !string.IsNullOrEmpty(scopeParam) ? context.Request.Query[scopeParam].ToString() : "";
            if (!string.IsNullOrEmpty(scopeValue))
            {
                cache.InvalidateByScope(scopeValue);
            }
            else
            {
                cache.InvalidateAll();
            }
        }

        // Stale-while-revalidate for GET requests to List* endpoints
        var isReadEndpoint = context.Request.Method == "GET"
            && endpoint.StartsWith("List", StringComparison.OrdinalIgnoreCase);

        // Computed once here and reused for the write-back below (was recomputed on the miss path).
        string? cacheKey = null;

        if (isReadEndpoint)
        {
            var cprof = CacheProfiler.Enabled;
            var t0 = cprof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var userRoleHash = CacheService.GetUserRoleHash(context);
            var t1 = cprof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            cacheKey = cache.BuildCacheKey(endpoint, context.Request.Query, userRoleHash);
            var t2 = cprof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var cached = await cache.Get(cacheKey, endpoint);
            if (cprof)
            {
                CacheProfiler.SetLogger(logger);
                CacheProfiler.RecordRequest(t1 - t0, t2 - t1, System.Diagnostics.Stopwatch.GetTimestamp() - t2, cached != null);
            }

            if (cached != null)
            {
                cache.Touch(cacheKey);
                var ttl = cache.GetTtl(endpoint);

                // Snapshot request BEFORE writing response — HttpContext becomes
                // unreliable after Response.WriteAsync completes
                Hashtable? requestSnapshot = null;
                if (cache.TryStartRefresh(cacheKey))
                {
                    requestSnapshot = await PowerShellRunnerService.SnapshotRequest(context);
                }

                context.Response.StatusCode = cached.Result.StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.Headers["X-Cache"] = cached.IsStale ? "HIT-STALE" : "HIT";
                context.Response.Headers["X-Cache-Age"] = $"{cached.Age.TotalSeconds:F0}s";
                context.Response.Headers["X-Cache-TTL"] = $"{ttl.TotalSeconds:F0}s";
                context.Response.Headers["X-Request-Duration"] = $"{requestSw.ElapsedMilliseconds}ms";
                await context.Response.WriteAsync(cached.Result.Body);

                // Kick off background refresh with the pre-captured snapshot
                if (requestSnapshot != null)
                {
                    var capturedEndpoint = endpoint;
                    var capturedCacheKey = cacheKey;
                    var capturedGeneration = cache.Generation;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await cache.WaitForBgRefreshSlot(capturedEndpoint);
                            var freshResult = await psRunner.ExecuteHttpScript(capturedEndpoint, requestSnapshot);
                            if (freshResult.StatusCode is >= 200 and < 400)
                            {
                                cache.SetIfSameGeneration(capturedCacheKey, freshResult, capturedGeneration);

                                // Check if the refreshed result wants to trigger an orchestrator run
                                if (freshResult.Body.Contains("_orchestratorTrigger"))
                                {
                                    try
                                    {
                                        using var doc = System.Text.Json.JsonDocument.Parse(freshResult.Body);
                                        if (doc.RootElement.TryGetProperty("_orchestratorTrigger", out var trigger) && trigger.GetBoolean())
                                        {
                                            var cmd = doc.RootElement.GetProperty("command").GetString()!;
                                            var planner = doc.RootElement.GetProperty("plannerScript").GetString()!;
                                            var taskScr = doc.RootElement.GetProperty("taskScript").GetString()!;
                                            var priority = doc.RootElement.TryGetProperty("priority", out var pVal) ? pVal.GetInt32() : 2;
                                            var plannerPath = psRunner.FindScript(planner);
                                            var taskPath = psRunner.FindScript(taskScr);
                                            if (plannerPath != null && taskPath != null)
                                            {
                                                _ = orchestrator.StartOrResumeRun(cmd, plannerPath, taskPath, priority, CancellationToken.None);
                                                logger.LogInformation("[API] Orchestrator triggered via bg refresh: {Command} P{Priority}", cmd, priority);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogWarning(ex, "Failed to parse orchestrator trigger from background refresh");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Background refresh failed: /API/{Endpoint}", capturedEndpoint);
                        }
                        finally
                        {
                            cache.ReleaseBgRefreshSlot(capturedEndpoint);
                            cache.FinishRefresh(capturedCacheKey);
                        }
                    });
                }
                return;
            }
        }

        logger.LogInformation("[HTTP] /API/{Endpoint} executing concurrent={Concurrent}",
            endpoint, concurrent);

        var result = await psRunner.ExecuteHttpScript(endpoint, context);

        // Invalidate cache on write operations (POST/PUT/DELETE/PATCH)
        if (context.Request.Method != "GET")
        {
            // If scope param is configured and present, only invalidate matching entries
            var scopeParam = cache.ScopeParam;
            var scopeValue = !string.IsNullOrEmpty(scopeParam) ? context.Request.Query[scopeParam].ToString() : "";
            if (!string.IsNullOrEmpty(scopeValue))
            {
                cache.InvalidateByScope(scopeValue);
            }
            else
            {
                // No scope context — can't scope the invalidation, clear everything
                cache.InvalidateAll();
            }
        }

        // Cache successful GET List* responses (reuse the key computed on the read path — no recompute).
        if (isReadEndpoint && cacheKey != null && result.StatusCode is >= 200 and < 400)
        {
            await cache.Set(cacheKey, result);
        }

        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers["X-Cache"] = "MISS";
        context.Response.Headers["X-Request-Duration"] = $"{requestSw.ElapsedMilliseconds}ms";

        // Check if the PS function wants us to trigger an orchestrator run
        if (result.StatusCode == 200 && result.Body.Contains("_orchestratorTrigger"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result.Body);
                if (doc.RootElement.TryGetProperty("_orchestratorTrigger", out var trigger) && trigger.GetBoolean())
                {
                    var cmd = doc.RootElement.GetProperty("command").GetString()!;
                    var planner = doc.RootElement.GetProperty("plannerScript").GetString()!;
                    var taskScr = doc.RootElement.GetProperty("taskScript").GetString()!;
                    var priority = doc.RootElement.TryGetProperty("priority", out var pVal) ? pVal.GetInt32() : 2;
                    var plannerPath = psRunner.FindScript(planner);
                    var taskPath = psRunner.FindScript(taskScr);
                    if (plannerPath != null && taskPath != null)
                    {
                        _ = orchestrator.StartOrResumeRun(cmd, plannerPath, taskPath, priority, CancellationToken.None);
                        logger.LogInformation("[API] Orchestrator triggered: {Command} P{Priority}", cmd, priority);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse orchestrator trigger from response");
            }
        }

        // Check if the PS function wants us to run a simple background script
        if (result.StatusCode == 200 && result.Body.Contains("_scriptTrigger"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result.Body);
                if (doc.RootElement.TryGetProperty("_scriptTrigger", out var trigger) && trigger.GetBoolean())
                {
                    var cmd = doc.RootElement.GetProperty("command").GetString()!;
                    var priority = doc.RootElement.TryGetProperty("priority", out var pVal) ? pVal.GetInt32() : 5;
                    var scriptPath = psRunner.FindScript(cmd);
                    if (scriptPath != null)
                    {
                        jobManager.Enqueue(cmd, priority, async ct =>
                        {
                            await psRunner.ExecuteScript(cmd);
                        });
                        logger.LogInformation("[API] Script triggered: {Command} P{Priority}", cmd, priority);
                    }
                    else
                    {
                        logger.LogWarning("[API] Script not found for trigger: {Command}", cmd);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse script trigger from response");
            }
        }

        // Check if the PS function wants us to cancel an orchestrator run
        if (result.StatusCode == 200 && result.Body.Contains("_cancelTrigger"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result.Body);
                if (doc.RootElement.TryGetProperty("_cancelTrigger", out var trigger) && trigger.GetBoolean())
                {
                    var cmd = doc.RootElement.GetProperty("command").GetString()!;
                    var (found, cancelledCount) = await orchestrator.CancelRunAsync(cmd);
                    if (!found)
                        (found, cancelledCount) = await orchestrator.CancelRunAsync($"Start-{cmd}");

                    if (found)
                    {
                        logger.LogInformation("[API] Run cancelled: {Command} ({Cancelled} pending tasks)", cmd, cancelledCount);
                        // Rewrite response with actual cancel result
                        result = new ScriptResult
                        {
                            StatusCode = 200,
                            Body = System.Text.Json.JsonSerializer.Serialize(new { name = cmd, cancelled = cancelledCount, message = $"Cancelled {cancelledCount} pending tasks" })
                        };
                    }
                    else
                    {
                        result = new ScriptResult
                        {
                            StatusCode = 404,
                            Body = System.Text.Json.JsonSerializer.Serialize(new { error = $"No run found for '{cmd}'" })
                        };
                    }
                    context.Response.StatusCode = result.StatusCode;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse cancel trigger from response");
            }
        }

        await context.Response.WriteAsync(result.Body);

    } // end try
    finally
    {
        var remaining = Interlocked.Decrement(ref activeRequests);
        requestSw.Stop();
        logger.LogDebug("[HTTP] /API/{Endpoint} done {ElapsedMs}ms remaining={Remaining}",
            endpoint, requestSw.ElapsedMilliseconds, remaining);
    }
});

} // end HTTP-role block (setup / jobs / PowerShell dispatch)

// Fallback: in dev mode, proxy to Next.js dev server for hot-reload.
// In production, try {path}.html first (Next.js static export), then index.html for SPA routing.
// Reuse the existing frontendFileProvider — do NOT create a new PhysicalFileProvider per request
// (PhysicalFileProvider allocates file watchers; creating per-request leaks handles)
app.MapFallback(async (HttpContext context) =>
{
    var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

    // No frontend on this node (Http/Background-only role, or Frontend/ absent) — nothing to fall back to.
    // Return 404 rather than faulting on a missing index.html.
    if (frontendFileProvider == null && devProxyClient == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Serve an HTML document, preferring a precomputed .br/.gz sibling when the client accepts it, so the
    // SPA fallback (index.html) and prerendered route pages go out precompressed with a fixed Content-Length
    // and ZERO per-request compression CPU — instead of being Brotli-compressed on the fly by
    // ResponseCompression (which strips Content-Length and chunks the response). Mirrors the precompressed
    // static middleware above; ResponseCompression sees Content-Encoding already set and passes through.
    async Task ServeHtmlAsync(string physicalHtmlPath)
    {
        var h = context.Response.Headers;
        context.Response.ContentType = "text/html";
        h.CacheControl = "no-cache, must-revalidate";

        string? enc = null, suffix = null;
        var accept = context.Request.Headers.AcceptEncoding.ToString();
        if (accept.Contains("br", StringComparison.OrdinalIgnoreCase)) { enc = "br"; suffix = ".br"; }
        else if (accept.Contains("gzip", StringComparison.OrdinalIgnoreCase)) { enc = "gzip"; suffix = ".gz"; }

        if (compressionEnabled && suffix != null)
        {
            var variant = new FileInfo(physicalHtmlPath + suffix);
            if (variant.Exists)
            {
                h.ContentEncoding = enc;
                h.Vary = "Accept-Encoding";
                h.ETag = $"\"{variant.LastWriteTimeUtc.ToFileTime():x}-{variant.Length:x}\"";
                context.Response.ContentLength = variant.Length;
                await context.Response.SendFileAsync(variant.FullName);
                return;
            }
        }

        // No precompressed sibling (a sub-1KB page, or compression disabled) — send raw with an explicit
        // Content-Length so the identity response is fixed-length, not chunked. If compression is enabled,
        // ResponseCompression may still compress this on the fly (replacing the length with chunked); when
        // compression is disabled it stays fixed-length identity.
        var raw = new FileInfo(physicalHtmlPath);
        if (raw.Exists)
        {
            h.ETag = $"\"{raw.LastWriteTimeUtc.ToFileTime():x}-{raw.Length:x}\"";
            context.Response.ContentLength = raw.Length;
        }
        await context.Response.SendFileAsync(physicalHtmlPath);
    }

    // Never SPA-fallback API or auth paths — return 404 so an unmatched /api, /API or /.auth path
    // isn't served index.html (a soft-200 that Cloudflare could cache against an API URL).
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/.auth", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Dev mode: proxy everything to Next.js dev server (Turbopack hot-reload)
    if (devProxyClient != null)
    {
        try
        {
            var targetUrl = path + context.Request.QueryString;
            using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, targetUrl);

            // Forward relevant headers
            foreach (var header in context.Request.Headers)
            {
                if (!header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                    proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            using var proxyResponse = await devProxyClient.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead);

            context.Response.StatusCode = (int)proxyResponse.StatusCode;
            foreach (var header in proxyResponse.Content.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in proxyResponse.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();

            // Remove transfer-encoding since ASP.NET handles chunking itself
            context.Response.Headers.Remove("transfer-encoding");

            await proxyResponse.Content.CopyToAsync(context.Response.Body);
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("[DevProxy] Next.js dev server not reachable: {Message}. Falling back to static files.", ex.Message);
            // Fall through to static file serving
        }
    }

    // Try serving {path}.html for Next.js static export pages
    if (frontendFileProvider != null && !string.IsNullOrEmpty(path) && path != "/" && !Path.HasExtension(path))
    {
        var htmlRelPath = (path + ".html").TrimStart('/');
        var fileInfo = frontendFileProvider.GetFileInfo(htmlRelPath);
        if (fileInfo.Exists && !fileInfo.IsDirectory && fileInfo.PhysicalPath != null)
        {
            await ServeHtmlAsync(fileInfo.PhysicalPath);
            return;
        }
    }

    // Fall back to index.html for SPA client-side routing
    var indexPath = Path.Combine(frontendPath, "index.html");
    await ServeHtmlAsync(indexPath);
});

app.Run();
