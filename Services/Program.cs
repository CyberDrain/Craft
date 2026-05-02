using System.Collections;
using System.Net.Http;
using CRAFT.Services;
using Microsoft.AspNetCore.ResponseCompression;
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
// Also register a singleton accessor for non-DI contexts
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CraftSettings>>().Value);

// Verbose logging controlled by environment variable
var verboseLogging = string.Equals(
    Environment.GetEnvironmentVariable("CRAFT_VERBOSE") ?? "false",
    "true", StringComparison.OrdinalIgnoreCase);
// ShowDebug: show Debug-level messages in console (noisy, off by default)
var showDebug = string.Equals(
    Environment.GetEnvironmentVariable("ShowDebug") ?? "false",
    "true", StringComparison.OrdinalIgnoreCase);

// File logging — writes to /log.txt (or current directory on Windows)
var logFilePath = OperatingSystem.IsLinux() ? "/log.txt" : Path.Combine(AppContext.BaseDirectory, "log.txt");
var logFileStream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
var logFileWriter = new StreamWriter(logFileStream) { AutoFlush = true };
builder.Logging.AddProvider(new FileLoggerProvider(logFileWriter, verboseLogging));

// Console: timestamps + suppress Debug unless ShowDebug is set
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});
if (!showDebug)
{
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
        level => level >= LogLevel.Information);
}

// Suppress noisy ASP.NET framework logging unless verbose
if (!verboseLogging)
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
        new[] { "application/json", "text/json" });
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
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobManager>());
builder.Services.AddHostedService<SchedulerService>();

var app = builder.Build();

// HTTP diagnostic listener — tracks DNS, TLS, socket connect, and HTTP request timing
// from ALL HttpClient instances (including those inside PowerShell's Invoke-RestMethod)
var httpDiagLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HttpDiag");
var httpListener = new HttpDiagnosticListener(httpDiagLogger, slowThresholdMs: 1000);
// Must keep reference alive — GC would collect it and stop events
app.Lifetime.ApplicationStopping.Register(() => httpListener.Dispose());

// Initialize ScriptRepository and WorkerPool
var repo = app.Services.GetRequiredService<ScriptRepository>();
repo.LoadAll(Path.Combine(AppContext.BaseDirectory, "API"));

var pool = app.Services.GetRequiredService<PowerShellWorkerPool>();
pool.Initialize();

var psRunner = app.Services.GetRequiredService<PowerShellRunnerService>();
var cache = app.Services.GetRequiredService<CacheService>();
var CraftSettings = app.Services.GetRequiredService<CraftSettings>();
var endpoints = psRunner.DiscoverHttpEndpoints();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

logger.LogInformation("[System] {AppName}: {Count} API endpoints discovered", CraftSettings.Name, endpoints.Count);
logger.LogInformation("[System] Pool: HTTP={Http} BG={Bg} verbose={Verbose}",
    CraftSettings.Worker.HttpPoolSize,
    CraftSettings.Worker.BgPoolSize,
    verboseLogging);

if (app.Environment.IsDevelopment())
{
    logger.LogWarning("[Auth] Running in Development mode \u2014 unauthenticated requests will receive dev principal with roles: {Roles}",
        string.Join(", ", CraftSettings.Auth.DevRoles));
}

// Response compression must be before static files
app.UseResponseCompression();

// Dev proxy: in Development mode, proxy frontend requests to `next dev` (hot-reload)
// instead of serving precompiled static files from Frontend/
var devFrontendUrl = app.Environment.IsDevelopment()
    ? Environment.GetEnvironmentVariable("CRAFT_DEV_FRONTEND_URL") ?? "http://localhost:3000"
    : null;
HttpClient? devProxyClient = null;
if (devFrontendUrl != null)
{
    devProxyClient = new HttpClient { BaseAddress = new Uri(devFrontendUrl) };
    devProxyClient.Timeout = TimeSpan.FromSeconds(30);
    logger.LogInformation("[System] Dev mode: proxying frontend to {Url}", devFrontendUrl);

    // In dev mode, intercept frontend requests (/_next/*, static assets, etc.)
    // and proxy them to the Next.js dev server before static file middleware runs
    app.Use(async (context, next) =>
    {
        var reqPath = context.Request.Path.Value ?? "";
        // Proxy to Next.js: /_next/*, /__nextjs, and any non-API path with a file extension
        // (e.g. /version.json, /manifest.json, /favicon.ico) that doesn't exist in Frontend/
        // Exclude server-local files like /log.txt
        var shouldProxy = reqPath.StartsWith("/_next/") || reqPath.StartsWith("/__nextjs")
            || (Path.HasExtension(reqPath)
                && !reqPath.StartsWith("/API/", StringComparison.OrdinalIgnoreCase)
                && !reqPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                && !reqPath.StartsWith("/.auth/", StringComparison.OrdinalIgnoreCase)
                && !reqPath.Equals("/log.txt", StringComparison.OrdinalIgnoreCase));
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

// Serve static files from Frontend/ (production mode, or fallback if dev proxy is down)
var frontendPath = Path.Combine(AppContext.BaseDirectory, "Frontend");
IFileProvider? frontendFileProvider = null;
if (Directory.Exists(frontendPath))
{
    frontendFileProvider = new PhysicalFileProvider(frontendPath);

    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = frontendFileProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = frontendFileProvider,
        ServeUnknownFileTypes = false,
        OnPrepareResponse = ctx =>
        {
            var path = ctx.Context.Request.Path.Value ?? "";
            if (path.StartsWith("/_next/static/") || path.EndsWith(".js") || path.EndsWith(".css"))
            {
                // Immutable hashed assets — cache for 6 months (matches SWA config)
                ctx.Context.Response.Headers.CacheControl = "public, max-age=15770000, immutable";
            }
            else if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".gif") ||
                     path.EndsWith(".ico") || path.EndsWith(".svg") || path.EndsWith(".xml"))
            {
                ctx.Context.Response.Headers.CacheControl = "public, max-age=15770000, must-revalidate";
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
// 3. CRAFT session cookie (no x-ms-client-principal)
//    → Build header from validated session
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // Setup redirect: when auth is not configured and not in dev mode,
    // redirect browser requests to the setup page and return 403 for API calls
    if (!authService.IsConfigured && !app.Environment.IsDevelopment()
        && !string.IsNullOrEmpty(CraftSettings.Auth.SetupPath))
    {
        var setupPath = CraftSettings.Auth.SetupPath;

        // Check for setup_token in query string — validate and create session
        var setupToken = context.Request.Query["setup_token"].ToString();
        if (!string.IsNullOrEmpty(setupToken))
        {
            var sessionId = authService.ValidateSetupToken(setupToken);
            if (sessionId != null)
            {
                authService.SetSessionCookie(context, sessionId);
                // Redirect to setup page without the token in the URL
                context.Response.Redirect(setupPath);
                return;
            }
            else
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(new { error = "Invalid or expired setup token" }));
                return;
            }
        }

        // Check if user has a valid setup session (from setup token)
        var setupSession = authService.GetSession(context);
        if (setupSession != null)
        {
            context.Items["CraftSession"] = setupSession;
            // Allow through — user authenticated via setup token
        }
        else
        {
            // No session — enforce setup redirect/block
            // Allow the setup page itself, its assets, and whitelisted API endpoints through
            if (!path.Equals(setupPath, StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/_next/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/.auth/", StringComparison.OrdinalIgnoreCase)
                && !Path.HasExtension(path))
            {
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    // Allow configured setup API paths through without auth
                    if (CraftSettings.Auth.SetupAllowedPaths.Exists(p =>
                        path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        await next();
                        return;
                    }
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        System.Text.Json.JsonSerializer.Serialize(new { setupRequired = true, setupPath }));
                    return;
                }
                context.Response.Redirect(setupPath);
                return;
            }
        }
    }

    // Always try to resolve CRAFT session and store it for /.auth/me to use
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
                // Extract UPN from claims
                string? upn = null;
                string? oid = null;
                if (root.TryGetProperty("claims", out var claims))
                {
                    foreach (var claim in claims.EnumerateArray())
                    {
                        var typ = claim.GetProperty("typ").GetString() ?? "";
                        var val = claim.GetProperty("val").GetString() ?? "";
                        if (typ == "preferred_username" || typ == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")
                            upn ??= val;
                        if (typ == "http://schemas.microsoft.com/identity/claims/objectidentifier")
                            oid ??= val;
                    }
                }

                if (!string.IsNullOrEmpty(upn))
                {
                    // Look up user in allowedUsers table for CIPP roles
                    var roles = await authService.GetUserRoles(upn);
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
                    context.Request.Headers["x-ms-client-principal-idp"] = "aad";
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

    // No x-ms-client-principal header — try CRAFT session cookie
    if (context.Items.TryGetValue("CraftSession", out var sessionObj) && sessionObj is AuthService.SessionData session2)
    {
        var headerValue = authService.BuildClientPrincipalHeader(session2);
        context.Request.Headers["x-ms-client-principal"] = headerValue;
        context.Request.Headers["x-ms-client-principal-idp"] = "aad";
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
        context.Request.Headers["x-ms-client-principal-idp"] = "aad";
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

// Logout: clears session and redirects
app.MapGet("/logout", (HttpContext context) =>
{
    authService.ClearSession(context);
    return Results.Redirect("/");
});

app.MapGet("/.auth/logout", (HttpContext context) =>
{
    authService.ClearSession(context);
    return Results.Redirect("/");
});

// /.auth/me — returns clientPrincipal in Azure SWA format for frontend compatibility
app.MapGet("/.auth/me", (HttpContext context) =>
{
    // If we have a CRAFT session, return clientPrincipal from validated token
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

// /api/me — returns clientPrincipal + permissions (routed through PowerShell Test-CIPPAccess)
// This is handled as a regular PS endpoint via the /API/{endpoint} route below

// /api/me endpoint — resolves permissions from user roles via PowerShell (if configured)
app.MapGet("/api/me", async (HttpContext context) =>
{
    var meFunction = CraftSettings.Auth.MeEndpointFunction;

    // If no PS function configured for /api/me, return the raw auth principal
    if (string.IsNullOrEmpty(meFunction))
    {
        if (context.Items.TryGetValue("CraftSession", out var sessionObj) && sessionObj is AuthService.SessionData session)
        {
            var principal = authService.BuildClientPrincipal(session);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { clientPrincipal = principal }));
        }
        else if (app.Environment.IsDevelopment())
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                clientPrincipal = new
                {
                    identityProvider = "aad",
                    userId = CraftSettings.Auth.DevUserId,
                    userDetails = CraftSettings.Auth.DevUserDetails,
                    userRoles = CraftSettings.Auth.DevRoles.ToArray()
                }
            }));
        }
        else
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { clientPrincipal = (object?)null }));
        }
        return;
    }

    // Route through PowerShell for full permission resolution
    var request = await PowerShellRunnerService.SnapshotRequest(context);
    var parms = (System.Collections.Hashtable)request["Params"]!;
    parms["CIPPEndpoint"] = meFunction;

    try
    {
        var result = await psRunner.ExecuteHttpEndpoint(meFunction, request);

        // /api/me must ALWAYS return 200 — the frontend uses clientPrincipal: null
        // as the "not authorized" signal, not HTTP status codes.
        if (result.StatusCode is >= 200 and < 300)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(result.Body);
        }
        else
        {
            logger.LogWarning("[Auth] /api/me PS returned {Status}: {Body}", result.StatusCode, result.Body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
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
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
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

// Serve the log file
app.MapGet("/log.txt", async (HttpContext context) =>
{
    if (!File.Exists(logFilePath))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("No log file found");
        return;
    }
    context.Response.ContentType = "text/plain; charset=utf-8";
    // Tail support: ?tail=N returns just the last N lines
    var tailParam = context.Request.Query["tail"].ToString();
    if (int.TryParse(tailParam, out var tailLines) && tailLines > 0)
    {
        using var fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var allLines = (await reader.ReadToEndAsync()).Split('\n');
        var start = Math.Max(0, allLines.Length - tailLines);
        await context.Response.WriteAsync(string.Join('\n', allLines[start..]));
    }
    else
    {
        using var fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await fs.CopyToAsync(context.Response.Body);
    }
});

// Fallback: in dev mode, proxy to Next.js dev server for hot-reload.
// In production, try {path}.html first (Next.js static export), then index.html for SPA routing.
// Reuse the existing frontendFileProvider — do NOT create a new PhysicalFileProvider per request
// (PhysicalFileProvider allocates file watchers; creating per-request leaks handles)
app.MapFallback(async (HttpContext context) =>
{
    var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

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
            context.Response.ContentType = "text/html";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            await context.Response.SendFileAsync(fileInfo.PhysicalPath);
            return;
        }
    }

    // Fall back to index.html for SPA client-side routing
    var indexPath = Path.Combine(frontendPath, "index.html");
    context.Response.ContentType = "text/html";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    await context.Response.SendFileAsync(indexPath);
});

// Process pending invites and log setup info if auth is not configured (first-run)
if (!authService.IsConfigured && !app.Environment.IsDevelopment()
    && !string.IsNullOrEmpty(CraftSettings.Auth.SetupPath))
{
    // Initial invite processing
    var urls = app.Urls.Any() ? string.Join(", ", app.Urls) : "http://+:8080";
    await authService.ProcessPendingInvitesAsync(urls);

    logger.LogWarning("╔══════════════════════════════════════════════════════════════╗");
    logger.LogWarning("║  FIRST-RUN SETUP — Authentication is not configured.       ║");
    logger.LogWarning("║  Add a user to the '{Table}' table to generate an invite:", authService.UserTableFullName);
    logger.LogWarning("║    PartitionKey = (any value, e.g. empty string)");
    logger.LogWarning("║    RowKey       = user@example.com");
    logger.LogWarning("║    Roles        = [\"superadmin\",\"authenticated\",\"anonymous\"]");
    logger.LogWarning("║    InviteStatus = PendingInvite");
    logger.LogWarning("║  CRAFT will generate the invite URL within 30 seconds.     ║");
    logger.LogWarning("╚══════════════════════════════════════════════════════════════╝");

    // Start background polling for new invite requests
    _ = Task.Run(async () =>
    {
        while (!authService.IsConfigured)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (authService.IsConfigured) break;
            await authService.ProcessPendingInvitesAsync(urls);
        }
        logger.LogInformation("[Auth] Invite polling stopped — auth is now configured");
    });
}

app.Run();
