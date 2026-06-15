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

// Static-only / web-content mode: disable the PowerShell worker pool, scheduler, job manager and all
// background services so the host boots instantly and only serves static frontend content.
var staticOnly = craftSettings.Worker.Disabled
    || string.Equals(Environment.GetEnvironmentVariable("CRAFT_STATIC_ONLY"), "true", StringComparison.OrdinalIgnoreCase);
// Dev-auth toggle: in static-only mode, serve a canned dev principal for /.auth/me + /api/me so the SPA
// boots and renders pages without the PowerShell backend. For local testing only — never in production.
var staticOnlyDevAuth = staticOnly
    && string.Equals(Environment.GetEnvironmentVariable("CRAFT_STATIC_ONLY_DEVAUTH"), "true", StringComparison.OrdinalIgnoreCase);

// Compression toggle: when false, the host serves all static content raw/identity — precompressed
// .br/.gz siblings are not served and on-the-fly ResponseCompression is not applied. Lets a downstream
// app turn compression off (e.g. an upstream CDN already compresses, or the content doesn't benefit) and
// lets the perf harness A/B compressed vs raw on the same image. Default true (from App:Frontend:Compression);
// the CRAFT_COMPRESSION environment variable (true/false) takes precedence when set.
var compressionEnabled = craftSettings.Frontend.Compression;
var compressionEnv = Environment.GetEnvironmentVariable("CRAFT_COMPRESSION");
if (!string.IsNullOrEmpty(compressionEnv))
    compressionEnabled = compressionEnv.Equals("true", StringComparison.OrdinalIgnoreCase) || compressionEnv == "1";

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

// Configure Kestrel timeout
// If KestrelTimeoutSeconds is explicitly set, use it.
// Otherwise, derive from HttpTimeoutSeconds if > 0, else default to 0 (no timeout).
var kestrelTimeout = craftSettings.KestrelTimeoutSeconds;
if (kestrelTimeout == 0 && craftSettings.Worker.HttpTimeoutSeconds > 0)
{
    kestrelTimeout = craftSettings.Worker.HttpTimeoutSeconds;
}

if (kestrelTimeout > 0)
{
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

        // Connection limits (adjust based on expected load)
        options.Limits.MaxConcurrentConnections = null;  // null = unlimited (let OS handle)
        options.Limits.MaxConcurrentUpgradedConnections = null;

        // Prevent slow-loris attacks — minimum data rates
        options.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
            bytesPerSecond: 240,   // 240 bytes/sec minimum
            gracePeriod: TimeSpan.FromSeconds(5));
        options.Limits.MinResponseDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
            bytesPerSecond: 240,
            gracePeriod: TimeSpan.FromSeconds(5));
    });
}

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
builder.Services.AddSingleton<ScriptRepository>();
builder.Services.AddSingleton<PowerShellWorkerPool>();
builder.Services.AddSingleton<PowerShellRunnerService>();
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<BackgroundTaskLimiter>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<OrchestratorTableStore>();
builder.Services.AddSingleton<OrchestratorService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddSingleton<StatsHistoryService>();
// Background hosted services (job manager, scheduler, stats history) only run when NOT static-only.
if (!staticOnly)
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

var app = builder.Build();

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

    // 3. Initialize PowerShell worker pool (loads modules, creates runspaces)
    pool.Initialize();

    // Pool is ready — clear the restart counter so we don't carry stale crash state
    healthMonitor.ClearRestartCounter();
}

if (staticOnly)
{
    logger.LogWarning("[System] STATIC-ONLY mode — PowerShell worker pool, scheduler, job manager and " +
        "background services are disabled. Serving static frontend content only; /api, /API, /.auth, " +
        "/login and /logout return 503.");
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

// Static-only mode: short-circuit all dynamic/API/auth endpoints with 503 so nothing touches the
// (disabled) PowerShell pipeline; everything else falls through to static file serving.
if (staticOnly)
{
    // Optional dev-auth (CRAFT_STATIC_ONLY_DEVAUTH=true): canned /.auth/me + /api/me so the SPA boots and
    // renders pages without the PS backend. Roles/permissions come from Auth.DevRoles / Auth.DevPermissions.
    var devRoles = CraftSettings.Auth.DevRoles.Count > 0
        ? CraftSettings.Auth.DevRoles.ToArray() : new[] { "superadmin", "authenticated" };
    var devPerms = CraftSettings.Auth.DevPermissions.Count > 0
        ? CraftSettings.Auth.DevPermissions.ToArray() : new[] { "*" };
    var devPrincipal = new
    {
        identityProvider = "aad",
        userId = CraftSettings.Auth.DevUserId,
        userDetails = CraftSettings.Auth.DevUserDetails,
        userRoles = devRoles
    };
    var authMeJson = System.Text.Json.JsonSerializer.Serialize(new { clientPrincipal = devPrincipal });
    var apiMeJson = System.Text.Json.JsonSerializer.Serialize(new { clientPrincipal = devPrincipal, permissions = devPerms });
    if (staticOnlyDevAuth)
        logger.LogWarning("[System] STATIC-ONLY dev-auth ENABLED — serving a canned superadmin principal for " +
            "/.auth/me and /api/me. For local testing only; never enable in production.");

    app.Use(async (context, next) =>
    {
        var p = context.Request.Path.Value ?? "";
        if (staticOnlyDevAuth && p.Equals("/.auth/me", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(authMeJson);
            return;
        }
        if (staticOnlyDevAuth && p.Equals("/api/me", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(apiMeJson);
            return;
        }
        if (p.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("/.auth", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("/logout", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"CRAFT static-only mode: API and auth are disabled.\"}");
            return;
        }
        await next();
    });
}

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

// Startup loading screen: while the worker pool is initializing, serve a loading page
// for browser requests and 503 for API calls. Health endpoint stays available for polling.
app.Use(async (context, next) =>
{
    if (!pool.IsReady && !staticOnly)
    {
        var path = context.Request.Path.Value ?? "";

        // Always let the health endpoint through for polling
        if (path.Equals("/api/setup/health", StringComparison.OrdinalIgnoreCase))
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
// instead of serving precompiled static files from Frontend/
var devFrontendUrl = app.Environment.IsDevelopment()
    ? Environment.GetEnvironmentVariable("CRAFT_DEV_FRONTEND_URL") ?? "http://localhost:3000"
    : null;
HttpClient? devProxyClient = null;
if (devFrontendUrl != null)
{
    devProxyClient = new HttpClient { BaseAddress = new Uri(devFrontendUrl) };
    devProxyClient.Timeout = TimeSpan.FromSeconds(120); // longer timeout for slow Next.js dev builds
    logger.LogInformation("[System] Dev mode: proxying frontend to {Url}", devFrontendUrl);

    // In dev mode, intercept frontend requests (/_next/*, static assets, etc.)
    // and proxy them to the Next.js dev server before static file middleware runs
    app.Use(async (context, next) =>
    {
        var reqPath = context.Request.Path.Value ?? "";
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

// Serve static files from Frontend/ (production mode, or fallback if dev proxy is down)
var frontendPath = Path.Combine(AppContext.BaseDirectory, "Frontend");
IFileProvider? frontendFileProvider = null;
if (Directory.Exists(frontendPath))
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

// Auth middleware: handles three auth scenarios:
// 1. Azure App Service EasyAuth (x-ms-client-principal in App Service format — has "claims", no "userRoles")
//    → Transform to SWA format with roles from allowedUsers table
// 2. Azure SWA EasyAuth (x-ms-client-principal in SWA format — has "userRoles")
//    → Pass through as-is
// 3. Craft session cookie (no x-ms-client-principal)
//    → Build header from validated session
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // Always try to resolve Craft session and store it for /.auth/me to use
    if (authService.IsConfigured)
    {
        var session = authService.GetSession(context);
        if (session != null)
        {
            context.Items["CraftSession"] = session;
        }
    }

    // Skip header injection for static files, login/logout endpoints, and /.auth/*
    if (path.StartsWith("/_next/") || path.StartsWith("/assets/") ||
        path.StartsWith("/.auth/") || path == "/login" || path == "/logout" ||
        Path.HasExtension(path))
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
                    // Service principal — emit SWA-format principal with idp=aad so downstream
                    // resolves AppName from the ApiClients table using x-ms-client-principal-name.
                    var spFormat = new
                    {
                        identityProvider = "aad",
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
                    var swaFormat = new
                    {
                        identityProvider = "aad",
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
            // else: already in SWA format (has userRoles) — pass through as-is
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth] Failed to parse x-ms-client-principal header");
            // Pass through untouched if we can't parse it
        }

        await next();
        return;
    }

    // No x-ms-client-principal header — try Craft session cookie
    if (context.Items.TryGetValue("CraftSession", out var sessionObj) && sessionObj is AuthService.SessionData session2)
    {
        var headerValue = authService.BuildClientPrincipalHeader(session2);
        context.Request.Headers["x-ms-client-principal"] = headerValue;
        context.Request.Headers["x-ms-client-principal-idp"] = "azureStaticWebApps";
        context.Request.Headers["x-ms-client-principal-name"] = session2.Upn;
    }
    else if (app.Environment.IsDevelopment())
    {
        // Local dev: inject a dev principal so no login is required
        logger.LogDebug("[Auth] Dev auth bypass: injecting dev principal for {Path}", path);
        var devPrincipal = new
        {
            identityProvider = "aad",
            userId = CraftSettings.Auth.DevUserId,
            userDetails = CraftSettings.Auth.DevUserDetails,
            userRoles = CraftSettings.Auth.DevRoles.ToArray()
        };
        var devJson = System.Text.Json.JsonSerializer.Serialize(devPrincipal);
        var devBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(devJson));
        context.Request.Headers["x-ms-client-principal"] = devBase64;
        context.Request.Headers["x-ms-client-principal-idp"] = "azureStaticWebApps";
        context.Request.Headers["x-ms-client-principal-name"] = CraftSettings.Auth.DevUserDetails;
    }

    await next();
});

// --- Login / Logout / Auth Endpoints ---

// Login: redirects to Azure AD
app.MapGet("/login", (HttpContext context) =>
{
    if (!authService.IsConfigured)
    {
        return Results.Problem("Authentication not configured. Set WEBSITE_AUTH_CLIENT_ID, AUTH_SECRET, WEBSITE_AUTH_AAD_ALLOWED_TENANTS.");
    }

    var postLoginRedirect = context.Request.Query["post_login_redirect_uri"].ToString();
    if (string.IsNullOrEmpty(postLoginRedirect)) postLoginRedirect = "/";

    var host = context.Request.Host.ToString();
    var scheme = context.Request.Scheme;
    var redirectUri = $"{scheme}://{host}/.auth/callback";

    var loginUrl = authService.GetLoginUrl(redirectUri, postLoginRedirect);
    return Results.Redirect(loginUrl);
});

// Also support the SWA-style login path for frontend compatibility
app.MapGet("/.auth/login/aad", (HttpContext context) =>
{
    if (!authService.IsConfigured)
    {
        return Results.Redirect("/");
    }

    var postLoginRedirect = context.Request.Query["post_login_redirect_uri"].ToString();
    if (string.IsNullOrEmpty(postLoginRedirect)) postLoginRedirect = "/";

    var host = context.Request.Host.ToString();
    var scheme = context.Request.Scheme;
    var redirectUri = $"{scheme}://{host}/.auth/callback";

    var loginUrl = authService.GetLoginUrl(redirectUri, postLoginRedirect);
    return Results.Redirect(loginUrl);
});

// OAuth callback: exchanges code for tokens, validates, creates session
app.MapGet("/.auth/callback", async (HttpContext context) =>
{
    var code = context.Request.Query["code"].ToString();
    var state = context.Request.Query["state"].ToString();
    var error = context.Request.Query["error"].ToString();

    if (!string.IsNullOrEmpty(error))
    {
        var errorDesc = context.Request.Query["error_description"].ToString();
        logger.LogWarning("[Auth] OAuth error: {Error} - {Desc}", error, errorDesc);
        context.Response.Redirect($"/unauthenticated?error={Uri.EscapeDataString(errorDesc)}");
        return;
    }

    if (string.IsNullOrEmpty(code))
    {
        context.Response.Redirect("/unauthenticated?error=No+authorization+code+received");
        return;
    }

    try
    {
        var host = context.Request.Host.ToString();
        var scheme = context.Request.Scheme;
        var redirectUri = $"{scheme}://{host}/.auth/callback";

        var (sessionId, redirectUrl) = await authService.HandleCallback(code, state, redirectUri);
        authService.SetSessionCookie(context, sessionId);
        context.Response.Redirect(redirectUrl);
    }
    catch (UnauthorizedAccessException ex)
    {
        logger.LogWarning("[Auth] Unauthorized: {Message}", ex.Message);
        context.Response.Redirect($"/unauthenticated?error={Uri.EscapeDataString(ex.Message)}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Auth] Callback failed");
        context.Response.Redirect($"/unauthenticated?error={Uri.EscapeDataString("Authentication failed. Please try again.")}");
    }
});

// Logout: clears session, invalidates user cache, and redirects
app.MapGet("/logout", (HttpContext context) =>
{
    authService.ClearSession(context);
    authService.InvalidateUserCache();
    return Results.Redirect("/");
});

app.MapGet("/.auth/logout", (HttpContext context) =>
{
    authService.ClearSession(context);
    authService.InvalidateUserCache();
    return Results.Redirect("/");
});

// /.auth/me — returns clientPrincipal in Azure SWA format for frontend compatibility
app.MapGet("/.auth/me", (HttpContext context) =>
{
    // If we have a Craft session, return clientPrincipal from validated token
    if (context.Items.TryGetValue("CraftSession", out var sessionObj) && sessionObj is AuthService.SessionData session)
    {
        var clientPrincipal = authService.BuildClientPrincipal(session);
        return Results.Json(new { clientPrincipal });
    }

    // Dev mode: return dev principal without requiring login
    if (app.Environment.IsDevelopment())
    {
        var devPrincipal = new
        {
            identityProvider = "aad",
            userId = CraftSettings.Auth.DevUserId,
            userDetails = CraftSettings.Auth.DevUserDetails,
            userRoles = CraftSettings.Auth.DevRoles.ToArray()
        };
        return Results.Json(new { clientPrincipal = devPrincipal });
    }

    // No session — return null clientPrincipal (frontend shows login)
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

        if (isReadEndpoint)
        {
            var userRoleHash = CacheService.GetUserRoleHash(context);
            var cacheKey = cache.BuildCacheKey(endpoint, context.Request.Query, userRoleHash);
            var cached = await cache.Get(cacheKey, endpoint);

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

        // Cache successful GET List* responses
        if (isReadEndpoint && result.StatusCode is >= 200 and < 400)
        {
            var userRoleHash = CacheService.GetUserRoleHash(context);
            var cacheKey = cache.BuildCacheKey(endpoint, context.Request.Query, userRoleHash);
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

// Fallback: in dev mode, proxy to Next.js dev server for hot-reload.
// In production, try {path}.html first (Next.js static export), then index.html for SPA routing.
// Reuse the existing frontendFileProvider — do NOT create a new PhysicalFileProvider per request
// (PhysicalFileProvider allocates file watchers; creating per-request leaks handles)
app.MapFallback(async (HttpContext context) =>
{
    var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

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
