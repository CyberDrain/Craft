namespace Craft.Services;

/// <summary>
/// Central configuration for the Craft (CyberDrain Runtime for Apps, Functions, Tasks) host.
/// All application-specific behavior is driven by these settings — the host itself
/// is generic. Bind from the "App" section of appsettings.json.
///
/// To onboard a new PowerShell application:
///   1. Place your compiled PS modules in API/Modules/
///   2. Place your frontend build in Frontend/
///   3. Configure this section in appsettings.json
///   4. Run the container
/// </summary>
public class CraftSettings
{
    /// <summary>Display name of the hosted application (used in logs and diagnostics).</summary>
    public string Name { get; set; } = "App";

    /// <summary>
    /// Controls when Kestrel starts accepting connections (when Azure marks the container as started).
    /// - Immediate: Kestrel starts first, init runs in background (default — shows loading page quickly)
    /// - HttpReady: Kestrel starts after HTTP worker pool is ready (API can serve on first request)
    /// - AllReady: Kestrel starts after all worker pools (HTTP + BG) are fully initialized
    /// Azure App Service has a 230s startup timeout — if init exceeds this, the container is killed.
    /// </summary>
    public string ReadinessMode { get; set; } = "Immediate";

    /// <summary>
    /// Kestrel request timeout in seconds. Controls how long Kestrel will wait for a complete request
    /// (headers + body) before aborting. Does NOT control how long the PowerShell script can run —
    /// use Worker.HttpTimeoutSeconds for that.
    /// 
    /// If not explicitly set (or set to 0):
    ///   - Derives from Worker.HttpTimeoutSeconds if > 0
    ///   - Otherwise defaults to 0 (no timeout)
    /// 
    /// Recommended: Set slightly higher than HttpTimeoutSeconds to give the worker time to respond
    /// before Kestrel drops the connection. Example: HttpTimeoutSeconds=120, KestrelTimeoutSeconds=130.
    /// </summary>
    public int KestrelTimeoutSeconds { get; set; } = 0;

    /// <summary>Worker configuration for the PowerShell runspace pools.</summary>
    public WorkerSettings Worker { get; set; } = new();

    /// <summary>Authentication and authorization settings.</summary>
    public AuthSettings Auth { get; set; } = new();

    /// <summary>Task scheduler configuration.</summary>
    public SchedulerSettings Scheduler { get; set; } = new();

    /// <summary>Orchestrator (fan-out/fan-in) configuration.</summary>
    public OrchestratorSettings Orchestrator { get; set; } = new();

    /// <summary>Response cache configuration.</summary>
    public CacheSettings Cache { get; set; } = new();

    /// <summary>File-backed log output with size-based rotation.</summary>
    public FileLoggingSettings FileLogging { get; set; } = new();

    /// <summary>Script repository — where to find PowerShell modules, HTTP endpoints, background scripts.</summary>
    public ScriptRepoSettings Scripts { get; set; } = new();

    /// <summary>Bootstrap setup — built-in first-run wizard for EasyAuth + app registration.</summary>
    public SetupSettings Setup { get; set; } = new();

    /// <summary>Historical stats collection — rolling time-series of worker/job metrics.</summary>
    public StatsHistorySettings StatsHistory { get; set; } = new();

    /// <summary>Container restart tracking — detects crash loops and forces worker reallocation.</summary>
    public ContainerHealthSettings ContainerHealth { get; set; } = new();

    /// <summary>Frontend serving policy — CSP header injection (EasyAuth handles auth/redirects).</summary>
    public FrontendSettings Frontend { get; set; } = new();

    /// <summary>
    /// Deployment roles (capabilities) — which parts of the host this process serves. One image, three
    /// independent switches. See <see cref="RolesSettings"/>. Also settable via the CRAFT_SERVE_FRONTEND /
    /// CRAFT_SERVE_API / CRAFT_RUN_BACKGROUND environment variables (which take precedence).
    /// </summary>
    public RolesSettings Roles { get; set; } = new();

    /// <summary>Health probe endpoint configuration. See <see cref="HealthSettings"/>.</summary>
    public HealthSettings Health { get; set; } = new();
}

/// <summary>
/// Role-agnostic health probe. Enabled by default at <c>/healthz</c>; a deployment can relocate it behind a
/// specific probe URL or turn it off entirely. Overridable via CRAFT_HEALTH_ENABLED / CRAFT_HEALTH_PATH.
/// </summary>
public class HealthSettings
{
    /// <summary>Whether the health endpoint is mapped. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path the health endpoint is served at. Default "/healthz". A leading slash is added if missing.</summary>
    public string Path { get; set; } = "/healthz";
}

/// <summary>
/// Deployment roles (capabilities). Each is a nullable bool: <c>null</c> = "not explicitly set".
///
/// Resolution (in Program.cs): if ANY of the three is explicitly set (here or via CRAFT_SERVE_*/CRAFT_RUN_*
/// env), the host uses exactly those (unset → off). Otherwise all three default on (the combined monolith).
///
/// Presets that fall out of the flags:
///   frontend        Frontend                       — pure static host (CDN origin), no PowerShell
///   http            Http                           — API node; can queue orchestrations, processed elsewhere
///   background      Background                     — worker node; scheduler + orchestrator processing
///   backend         Http + Background              — self-contained API + workers, no frontend
///   frontend+http   Frontend + Http                — app node without background workers
///   combined        Frontend + Http + Background   — the default monolith
/// </summary>
public class RolesSettings
{
    /// <summary>Serve static web content from Frontend/. Null = not explicitly set.</summary>
    public bool? Frontend { get; set; }

    /// <summary>Serve /api + auth via the HTTP PowerShell pool. Null = not explicitly set.</summary>
    public bool? Http { get; set; }

    /// <summary>Run scheduler / orchestrator / job-manager / stats via the BG pool. Null = not explicitly set.</summary>
    public bool? Background { get; set; }
}

/// <summary>
/// Frontend response policy. EasyAuth handles anon redirects and auth at the platform layer;
/// this exists for response headers EasyAuth doesn't touch.
/// </summary>
public class FrontendSettings
{
    /// <summary>
    /// Content-Security-Policy header value applied to all responses.
    /// Mirrors what SWA's globalHeaders.content-security-policy did.
    /// Null/empty = no CSP set.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary>
    /// Whether the host compresses static responses. Default true.
    /// - true: serves precompressed .br/.gz sibling files when present, and on-the-fly Brotli/Gzip as a
    ///   fallback for anything without a sibling.
    /// - false: serves everything raw/identity (no precompressed files served, ResponseCompression off).
    /// Turn off when an upstream CDN (e.g. Cloudflare) already compresses, when the content does not
    /// benefit, or to A/B measure compressed vs raw serving. Overridable via the CRAFT_COMPRESSION
    /// environment variable (true/false), which takes precedence over this setting.
    /// </summary>
    public bool Compression { get; set; } = true;
}

/// <summary>
/// PowerShell worker pool and initialization settings.
/// </summary>
public class WorkerSettings
{
    /// <summary>Number of workers reserved for HTTP request handling.</summary>
    public int HttpPoolSize { get; set; } = 2;

    /// <summary>Number of workers reserved for background jobs (scheduler, orchestrator, queue).</summary>
    public int BgPoolSize { get; set; } = 4;

    /// <summary>
    /// Optional list of host profiles that override HttpPoolSize/BgPoolSize based on the
    /// detected runtime environment (WEBSITE_SKU + Environment.ProcessorCount).
    /// First matching entry wins. No match (or any parse failure) leaves the baseline values
    /// from HttpPoolSize/BgPoolSize untouched.
    /// </summary>
    public List<SkuProfile> SkuProfiles { get; set; } = [];

    /// <summary>
    /// When true, SkuProfiles are ignored entirely and the configured baseline
    /// HttpPoolSize/BgPoolSize are always used. Kill-switch for downstream apps
    /// that want to opt out of host-tier scaling.
    /// </summary>
    public bool IgnoreSkuProfiles { get; set; } = false;

    /// <summary>
    /// Maximum execution time in seconds for a single HTTP request handler.
    /// When exceeded, the PowerShell pipeline is stopped and the worker is reclaimed.
    /// 0 = no timeout (default). Recommended: 120-300 for HTTP endpoints.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Maximum execution time in seconds for a single background job (scheduler, orchestrator task).
    /// When exceeded, the PowerShell pipeline is stopped and the worker is reclaimed.
    /// 0 = no timeout (default). Recommended: 600-3600 for background jobs.
    /// </summary>
    public int BgTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Environment variables to inject into every PowerShell runspace.
    /// Use "{ApiBasePath}" as a placeholder — it will be replaced with the resolved API directory at startup.
    /// Example: { "MyAppRoot": "{ApiBasePath}", "AppMode": "container" }
    /// </summary>
    public Dictionary<string, string> EnvVars { get; set; } = new();

    /// <summary>
    /// Additional env var names that should be set to the API root path (alongside CRAFT_ROOT).
    /// Use this so existing scripts can reference their own root variable without changes.
    /// Example: ["CIPPRootPath", "CIPPRoot"] → $env:CIPPRootPath and $env:CIPPRoot are set to the API root.
    /// </summary>
    public List<string> RootPathVars { get; set; } = [];

    /// <summary>
    /// PowerShell scripts to run once on a worker after module import for process-level warmup.
    /// Typically used for credential loading (Key Vault), cache priming, or connection setup.
    /// Errors are non-fatal (logged as warnings). Env vars set here are process-level and
    /// visible to all workers.
    /// Example: ["Initialize-CIPPAuth | Out-Null", "Get-Tenants -IncludeErrors | Out-Null"]
    /// </summary>
    public List<string> WarmupScripts { get; set; } = [];

    /// <summary>
    /// Controls when WarmupScripts run relative to the HTTP ready signal.
    ///
    /// "BeforeReady" — Runs on the first HTTP worker BEFORE signaling HTTP ready.
    ///                  Guarantees warmup is complete before any request is served.
    ///                  Adds warmup time (~8-10s) to HTTP ready latency.
    ///                  Best when: requests will fail without warmup state (e.g. env vars).
    ///
    /// "AfterReady"  — (default) Runs on the first HTTP worker AFTER signaling HTTP ready.
    ///                  HTTP starts accepting requests immediately; warmup runs in parallel.
    ///                  The first worker is briefly unavailable during warmup (pool has 1 less).
    ///                  Best when: warmup is idempotent and can race with early requests.
    ///
    /// "Background"  — Runs on the first BG worker during background pool initialization.
    ///                  HTTP pool is completely unaffected. Warmup happens ~15-20s after
    ///                  HTTP ready (when BG first-worker comes up).
    ///                  Best when: warmup state isn't needed for HTTP requests, or callers
    ///                  handle missing state gracefully (e.g. lazy credential loading).
    /// </summary>
    public string WarmupMode { get; set; } = "AfterReady";

    /// <summary>
    /// Recycle (dispose + replace) a worker after this many invocations to reclaim
    /// native memory leaked by the PowerShell runtime. 0 = never recycle (default).
    /// Recommended: 100-500 for long-running workloads.
    /// </summary>
    public int RecycleAfterInvocations { get; set; } = 0;

    /// <summary>
    /// Run each worker's PowerShell pipeline on one reused thread (PSThreadOptions.ReuseThread) instead of
    /// spinning a new thread per invocation. Default true. This is the single biggest per-request dispatch
    /// win (thread creation was ~50% of the PS-invoke cost — see docs/dispatch-analysis.md) and matches how
    /// the Azure Functions PowerShell worker keeps a persistent runspace. Safe because each worker owns one
    /// runspace and serves one request at a time. Set false only to A/B or if a module misbehaves on a
    /// long-lived pipeline thread.
    /// </summary>
    public bool ReuseRunspaceThread { get; set; } = true;

    /// <summary>
    /// Assemblies (.dll) to load into each runspace, relative to the API base path.
    /// Example: ["Shared/MyLib/bin/MyLib.dll"]
    /// </summary>
    public List<string> SharedAssemblies { get; set; } = [];

    /// <summary>
    /// Inject shared caches (Synchronized Hashtables) into specific module scopes.
    /// This enables cross-runspace token/state sharing without process-level statics.
    /// </summary>
    public List<ModuleInjection> ModuleInjections { get; set; } = [];

    /// <summary>
    /// PowerShell scripts to run after initialization on each worker (not just the first).
    /// Runs after module import and function deployment.
    /// Example: ["$global:AppConfig = Get-Content config.json | ConvertFrom-Json"]
    /// </summary>
    public List<string> PostInitScripts { get; set; } = [];

    /// <summary>
    /// Module names to skip during ISS import (e.g. test modules, legacy entrypoints).
    /// </summary>
    public List<string> SkipModules { get; set; } = [];

    /// <summary>
    /// Module names to load for HTTP workers. If empty, loads all modules (minus SkipModules).
    /// When specified, only these modules are imported into HTTP worker runspaces.
    /// Example: ["CIPPCore", "CIPPHTTP", "AzBobbyTables"]
    /// </summary>
    public List<string> HttpModules { get; set; } = [];

    /// <summary>
    /// Module names to load for background workers. If empty, loads all modules (minus SkipModules).
    /// When specified, only these modules are imported into BG worker runspaces.
    /// Example: ["CIPPCore", "CIPPStandards", "CIPPAlerts", "AzBobbyTables"]
    /// </summary>
    public List<string> BgModules { get; set; } = [];

    /// <summary>
    /// DEV ONLY. When true, before any worker imports modules the host rewrites each targeted
    /// module manifest's wildcard <c>FunctionsToExport = '*'</c> into the explicit list of
    /// functions the module exports (derived from its Public/*.ps1 files). This restores
    /// PowerShell name-based command auto-loading for modules that are NOT eagerly imported
    /// into a given pool (see HttpModules/BgModules) — matching what the production ModuleBuilder
    /// build bakes into the manifest. Without it, <c>Get-Command -Name Foo</c> / <c>&amp; Foo</c>
    /// cannot resolve a wildcard-export module unless it was already imported, so background
    /// dispatchers that probe with Get-Command fail with "not found".
    ///
    /// The export list is regenerated from the current Public/*.ps1 set on every run — whether the
    /// manifest currently holds a wildcard or a previously-written explicit list — so functions added
    /// since the last run are picked up. The manifest is only written when the result differs, so an
    /// already-current manifest leaves the file (and the dev file-watcher) stable. It mutates the
    /// on-disk manifests, so only enable when running from bind-mounted source (local dev). Never
    /// enable in production. Also settable via the environment variable CRAFT_DEV_EXPAND_EXPORTS=true.
    /// </summary>
    public bool DevExpandModuleExports { get; set; }

    /// <summary>
    /// DEV ONLY. Module names to expand when DevExpandModuleExports is true.
    /// Empty (default) = every module under Modules/ (minus SkipModules) that uses a wildcard export.
    /// </summary>
    public List<string> DevExpandModules { get; set; } = [];

    /// <summary>
    /// JSON files to preload into PowerShell variables at worker init.
    /// Each entry specifies a file path (relative to API/Config/), a variable name,
    /// and a scope ("global" or "env"). This replaces the need for hardcoded
    /// permission/role loading — any JSON file can be injected generically.
    /// </summary>
    public List<GlobalJsonPreload> JsonPreloads { get; set; } = [];
}

/// <summary>
/// Maps a host environment (an env var value and/or CPU count) to a worker pool sizing.
/// Used by the SkuProfiles list on WorkerSettings.
///
/// Matching:
///   - SkuEnv: name of the env var to read (e.g. "WEBSITE_SKU" on Azure App Service).
///             Null or empty = SKU criterion is wildcard (match any host).
///   - Sku:    expected value of that env var, compared case-insensitively
///             (e.g. "Basic", "PremiumV3"). Ignored when SkuEnv is empty.
///   - Cpu:    compared to Environment.ProcessorCount. Null or 0 = match any count.
///
/// All specified criteria must match. First matching entry in the list wins.
/// Letting the profile name the env var means downstream apps can target any host
/// (Azure, AWS, GCP, k8s, plain Docker) without backend changes — just point at
/// whatever env var the operator uses to identify the host tier.
/// </summary>
public class SkuProfile
{
    /// <summary>Name of the env var to read for the SKU identifier (e.g. "WEBSITE_SKU"). Null/empty = wildcard.</summary>
    public string? SkuEnv { get; set; }

    /// <summary>Expected value of the env var named by SkuEnv (e.g. "Basic"). Compared case-insensitively.</summary>
    public string? Sku { get; set; }

    /// <summary>CPU count to match (Environment.ProcessorCount). Null or 0 = match any count.</summary>
    public int? Cpu { get; set; }

    /// <summary>HTTP worker pool size to apply when this profile matches.</summary>
    public int HttpPoolSize { get; set; }

    /// <summary>Background worker pool size to apply when this profile matches.</summary>
    public int BgPoolSize { get; set; }
}

/// <summary>
/// A JSON file to preload into a PowerShell variable at worker startup.
/// </summary>
public class GlobalJsonPreload
{
    /// <summary>Path to the JSON file, relative to the API base path.</summary>
    public string File { get; set; } = "";

    /// <summary>Variable name (without $ prefix) to store the parsed content.</summary>
    public string Variable { get; set; } = "";

    /// <summary>
    /// Scope: "global" sets $global:VarName, "env" sets $env:VarName (raw JSON string).
    /// </summary>
    public string Scope { get; set; } = "global";

    /// <summary>
    /// If true, deserializes as a case-insensitive Hashtable instead of PSObject.
    /// Only applies when Scope is "global".
    /// </summary>
    public bool AsHashtable { get; set; } = false;
}

/// <summary>
/// Describes a shared variable to inject into a PowerShell module's scope.
/// The host maintains a process-wide Synchronized Hashtable and injects it
/// into the named module's script scope on each worker.
/// </summary>
public class ModuleInjection
{
    /// <summary>Module name to inject into (e.g. "CIPPCore").</summary>
    public string Module { get; set; } = "";

    /// <summary>Variable name in the module's script scope (e.g. "classictoken").</summary>
    public string Variable { get; set; } = "";

    /// <summary>
    /// Unique key for the shared cache instance. Multiple injections with the same
    /// CacheKey share the same Synchronized Hashtable across all workers.
    /// </summary>
    public string CacheKey { get; set; } = "";
}

/// <summary>
/// Authentication settings. The host supports Azure AD OIDC out of the box.
/// </summary>
public class AuthSettings
{
    /// <summary>Session cookie name.</summary>
    public string CookieName { get; set; } = "craft-session";

    /// <summary>
    /// Azure Table name for user authorization (UPN → roles).
    /// Override via Auth__UserTableName env var or in appsettings.
    /// Sanitized at runtime (alphanumeric only, 3-63 chars).
    /// </summary>
    public string UserTableName { get; set; } = "allowedUsers";

    /// <summary>
    /// Storage connection string for the allowedUsers table.
    /// If empty, falls back to AzureWebJobsStorage (same storage as the rest of the app).
    /// Set this to isolate the user table in a separate storage account.
    /// </summary>
    public string UserStorageConnection { get; set; } = "";

    /// <summary>
    /// Roles assigned to the dev-mode auto-login principal.
    /// Default: empty — set via appsettings (e.g. ["superadmin", "authenticated", "anonymous"]).
    /// Do NOT set defaults here — .NET config binding appends to list initializers, causing duplicates.
    /// </summary>
    public List<string> DevRoles { get; set; } = [];

    /// <summary>User ID for the dev-mode auto-login principal.</summary>
    public string DevUserId { get; set; } = "00000000-0000-0000-0000-000000000000";

    /// <summary>User details (UPN/email) for the dev-mode auto-login principal.</summary>
    public string DevUserDetails { get; set; } = "developer@localhost";

    /// <summary>
    /// PowerShell function name dispatched for /api/me. If empty, the literal "me"
    /// is used as the endpoint name. The PS function (or its MeEndpointHandler wrapper)
    /// owns the response shape — /api/me passes status code and body through unchanged.
    /// </summary>
    public string MeEndpointFunction { get; set; } = "";

    /// <summary>
    /// Optional wrapper PowerShell function invoked for /api/me instead of MeEndpointFunction
    /// directly. When set, this handler is called with the standard Request/TriggerMetadata
    /// parameters and is expected to dispatch internally based on Request.Params.CIPPEndpoint
    /// (which is set to MeEndpointFunction).
    /// When empty (default), MeEndpointFunction is invoked directly.
    /// Example (CIPP): "New-CippCoreRequest" with MeEndpointFunction = "me".
    /// </summary>
    public string MeEndpointHandler { get; set; } = "";

    /// <summary>
    /// When true, any user who authenticates against the configured AAD tenant
    /// is allowed in — even if they are not in the allowedUsers table.
    /// Users not in the table get ["authenticated", "anonymous"] as default roles.
    /// The hosted app (e.g. CIPP) can then do its own role resolution (e.g. via Entra group mapping).
    /// When false, only users explicitly listed in the allowedUsers table can log in.
    /// Override via Auth__AllowAllTenantUsers env var.
    /// </summary>
    public bool AllowAllTenantUsers { get; set; } = true;
}

/// <summary>
/// Scheduler settings — drives the background cron-based task system.
/// </summary>
public class SchedulerSettings
{
    /// <summary>
    /// Path to the scheduler task definitions, relative to the API directory.
    /// Examples: "Config/CIPPTimers.json", "timers.json"
    /// Must be a JSON array of SchedulerTask objects.
    /// </summary>
    public string ConfigFile { get; set; } = "SchedulerTasks.json";

    /// <summary>How often (in seconds) the scheduler checks for due tasks.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// When true, applies the configured timezone to ALL scheduler tasks,
    /// regardless of individual TZOffset settings. When false (default),
    /// only tasks with TZOffset=true use the configured timezone.
    /// </summary>
    public bool ApplyTZOffset { get; set; } = false;

    /// <summary>
    /// IANA or Windows timezone ID for timezone-aware cron evaluation.
    /// Overridable via env var App__Scheduler__Timezone (or CraftTZ at startup).
    /// When empty, all cron evaluation uses UTC.
    /// Examples: "America/New_York", "Europe/London", "Eastern Standard Time"
    /// </summary>
    public string Timezone { get; set; } = "";
}

/// <summary>
/// Orchestrator settings — fan-out/fan-in task execution with crash recovery.
/// </summary>
public class OrchestratorSettings
{
    /// <summary>
    /// Prefix for the three Azure Tables used by the orchestrator.
    /// Tables created: {Prefix}Runs, {Prefix}Tasks, {Prefix}Results.
    /// </summary>
    public string TablePrefix { get; set; } = "Orchestrator";

    /// <summary>
    /// Batch and coalesce per-task/run status writes through OrchestratorStatusWriter instead of writing each
    /// individually. Removes the per-task Azure Table write from the fan-out critical path (the throughput
    /// ceiling — see docs/orch-analysis.md). Default true. Results are never batched (their chunking path is
    /// untouched). Set false to fall back to the original per-task writes (for A/B).
    /// </summary>
    public bool BatchStatusWrites { get; set; } = true;

    /// <summary>
    /// When batching status writes, write the pre-invoke "Running" marker under a synchronous barrier so it
    /// is durable BEFORE the task invokes (batched with other concurrently-starting tasks). Preserves the
    /// AttemptCount/MaxRetries poison-task guarantee. Default true. False = eventual (faster, weaker: the
    /// marker rides the periodic flush, so a host crash within the flush window may not advance AttemptCount).
    /// </summary>
    public bool DurableRunningBarrier { get; set; } = true;

    /// <summary>How often (ms) the status writer flushes coalesced writes. Also the barrier latency ceiling.
    /// Default 25.</summary>
    public int StatusFlushIntervalMs { get; set; } = 25;

    /// <summary>
    /// PowerShell function used to execute individual orchestrator tasks.
    /// Receives a hashtable with task parameters.
    /// </summary>
    public string GenericTaskFunction { get; set; } = "Invoke-CraftTask";

    /// <summary>
    /// PowerShell function used to process queued commands.
    /// Receives Cmdlet + ParametersJson.
    /// </summary>
    public string QueueTaskFunction { get; set; } = "Invoke-CraftQueueTask";

    /// <summary>
    /// PowerShell function called after all tasks in a run complete.
    /// Receives the run name and result data.
    /// </summary>
    public string PostExecFunction { get; set; } = "Invoke-CraftPostExecution";

    /// <summary>Maximum number of times a task can be interrupted before being marked Failed.</summary>
    public int MaxRetries { get; set; } = 3;
}

/// <summary>
/// File-backed logging with size-based rotation.
/// Logs are written to {Directory}/{FilePrefix}.log and rotated to
/// {FilePrefix}.1.log, {FilePrefix}.2.log, etc. when MaxFileSizeMB is exceeded.
/// Oldest files beyond MaxFileCount are automatically deleted.
/// </summary>
public class FileLoggingSettings
{
    /// <summary>
    /// Directory for log files. On Linux defaults to "/logs", on Windows to "{BaseDirectory}/logs".
    /// Override via App__FileLogging__Directory env var.
    /// </summary>
    public string Directory { get; set; } = "";

    /// <summary>
    /// Filename prefix for log files. Files are named: {prefix}.log (current),
    /// {prefix}.1.log (previous), {prefix}.2.log, etc.
    /// </summary>
    public string FilePrefix { get; set; } = "craft";

    /// <summary>Maximum size in MB before rotating the current log file.</summary>
    public int MaxFileSizeMB { get; set; } = 25;

    /// <summary>Maximum number of rotated log files to retain. Oldest are deleted first.</summary>
    public int MaxFileCount { get; set; } = 10;

    /// <summary>
    /// Timestamp format for log entries. Must be a valid .NET DateTime format string.
    /// Default includes full date for accurate log filtering.
    /// </summary>
    public string TimestampFormat { get; set; } = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>
    /// Include the logger category name in log output.
    /// When true:  "2026-05-13 10:30:00.000 [INF] [Microsoft.AspNetCore.Routing] Matched endpoint"
    /// When false: "2026-05-13 10:30:00.000 [INF] Matched endpoint"
    /// </summary>
    public bool IncludeCategory { get; set; } = false;

    /// <summary>
    /// Minimum log level for file and console output AND PowerShell stream capture.
    /// Controls which PS preference variables are set to 'Continue' in runspaces:
    ///   - "Error"       → only Write-Error captured
    ///   - "Warning"     → Write-Error, Write-Warning
    ///   - "Information" → (default) Write-Error, Write-Warning, Write-Information/Write-Host
    ///   - "Debug"       → all above + Write-Debug (also suppresses ASP.NET framework noise filtering)
    ///   - "Trace"       → all above + Write-Verbose
    /// Also overridable via CRAFT_LOG_LEVEL environment variable.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>Resolved directory path, applying platform defaults when Directory is empty.</summary>
    internal string ResolvedDirectory => !string.IsNullOrEmpty(Directory)
        ? Directory
        : OperatingSystem.IsLinux() ? "/logs" : Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>Parse the configured LogLevel string into a .NET LogLevel enum value.</summary>
    internal Microsoft.Extensions.Logging.LogLevel ParsedLogLevel
    {
        get
        {
            // Allow env var override
            var envLevel = Environment.GetEnvironmentVariable("CRAFT_LOG_LEVEL");
            var level = !string.IsNullOrEmpty(envLevel) ? envLevel : LogLevel;
            return Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(level, ignoreCase: true, out var parsed)
                ? parsed
                : Microsoft.Extensions.Logging.LogLevel.Information;
        }
    }
}

/// <summary>
/// Response cache settings.
/// </summary>
public class CacheSettings
{
    /// <summary>
    /// Whether the in-memory/disk-backed API response cache is active. Null (default) = auto: on only when
    /// the node serves BOTH a browser UI and its API (combined / frontend+http roles), off for api-only,
    /// worker-only and static-only nodes. Set true/false to force it on or off in any role.
    /// Overridable via the CRAFT_RESPONSE_CACHE environment variable (true/false), which takes precedence.
    /// When disabled, no _cache/ directory is created or scanned and all get/set operations are no-ops.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Maximum number of cached responses in memory.</summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>
    /// Budget (bytes) for keeping cached response bodies in memory (an LRU tier over the disk cache) so a
    /// cache HIT returns from RAM instead of re-reading + re-decoding the file every time. Default 64 MiB.
    /// 0 disables the in-memory tier (disk-only — every hit reads the file). The index is always in memory;
    /// this only governs the hot bodies. See docs/cache-analysis.md.
    /// </summary>
    public long MaxMemoryBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Default TTL in seconds for cached responses.</summary>
    public int DefaultTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Query parameter name that triggers cache invalidation when set to "true".
    /// </summary>
    public string InvalidateParam { get; set; } = "InvalidateCache";

    /// <summary>
    /// Query parameter name used for scoped cache invalidation (e.g. per-tenant).
    /// When a write operation includes this parameter, only cache entries
    /// containing this parameter value are invalidated.
    /// </summary>
    public string ScopeParam { get; set; } = "";

    /// <summary>
    /// Per-endpoint TTL overrides. Key = endpoint name, Value = TTL in seconds.
    /// Example: { "ListTenants": 300, "ListUsers": 120 }
    /// </summary>
    public Dictionary<string, int> EndpointTtl { get; set; } = new();
}

/// <summary>
/// Script repository settings — controls where the host finds PowerShell scripts.
/// </summary>
public class ScriptRepoSettings
{
    /// <summary>
    /// Module directory names to scan for HTTP endpoint functions.
    /// Functions are mapped to /api/{route} endpoints. If a function starts with
    /// "Invoke-", that prefix is stripped for the route (e.g. Invoke-ListUsers → /api/ListUsers).
    /// Functions without the prefix use their full name as the route.
    /// </summary>
    public List<string> HttpModules { get; set; } = [];

    /// <summary>
    /// Optional global handler function name. When set, ALL /API/{endpoint} routes are
    /// dispatched through this single function instead of invoking the route's function
    /// directly. The endpoint name is passed via Request.Params.CIPPEndpoint so the handler
    /// can dispatch internally. Route lookup still happens for 404 detection, but the
    /// handler runs instead of the looked-up function.
    ///
    /// Use this when the hosted app's design assumes every HTTP endpoint goes through a
    /// common router (e.g. CIPP's New-CippCoreRequest, which performs Test-CIPPAccess
    /// authorization, telemetry, and feature-flag checks before dispatching to Invoke-*).
    ///
    /// Empty (default) = each /API/{endpoint} invokes Invoke-{endpoint} directly.
    /// Example (CIPP): "New-CippCoreRequest"
    /// </summary>
    public string HttpHandler { get; set; } = "";

    /// <summary>
    /// Directory names (relative to API/) to scan for background/timer scripts.
    /// </summary>
    public List<string> BackgroundScriptDirs { get; set; } = [];

    /// <summary>
    /// Permission extraction settings. When enabled, the host scans the configured
    /// modules for .ROLE and .FUNCTIONALITY comment-based help metadata and writes
    /// a JSON permissions map at startup. Disable if your app doesn't use RBAC metadata.
    /// </summary>
    public PermissionExtractionSettings PermissionExtraction { get; set; } = new();
}

/// <summary>
/// Controls whether and how the host extracts RBAC metadata from PowerShell functions.
/// When enabled, comment-based help tags (.ROLE, .FUNCTIONALITY) are scanned and
/// written to a JSON file for the auth layer to consume at runtime.
/// </summary>
public class PermissionExtractionSettings
{
    /// <summary>Whether permission extraction is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Module directory names (under API/Modules/) to scan for permission metadata.
    /// Can be the same as or different from HttpModules. For a monolithic module that
    /// contains both HTTP endpoints and background functions, just list it here.
    /// </summary>
    public List<string> Modules { get; set; } = [];

    /// <summary>
    /// Output file path for the generated permissions map (relative to the API base path).
    /// </summary>
    public string OutputFile { get; set; } = "Config/function-permissions.json";
}

/// <summary>
/// Bootstrap setup settings — enables a first-run wizard that creates the EasyAuth
/// app registration and configures App Service authentication automatically.
/// When enabled and EasyAuth is not yet configured, Craft serves a built-in setup UI
/// and blocks all application API endpoints until setup is complete.
/// </summary>
public class SetupSettings
{
    /// <summary>
    /// Enable the built-in bootstrap setup mode.
    /// When true, Craft registers setup routes and middleware.
    /// When false, setup routes are never registered regardless of auth state.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Public client ID used for the PKCE login popup during automated setup.
    /// Defaults to Microsoft's Azure PowerShell first-party app which supports
    /// auth code + PKCE without a client secret.
    /// </summary>
    public string BootstrapClientId { get; set; } = "1950a258-227b-4e31-a9cf-717495945fc2";

    /// <summary>
    /// Display name for the created EasyAuth app registration.
    /// Uses the App.Name setting to generate: "Craft-EasyAuth-{Name}".
    /// Override this to set a custom name.
    /// </summary>
    public string AuthAppDisplayName { get; set; } = "";

    /// <summary>
    /// Action taken when an unauthenticated request arrives.
    /// Applied to globalValidation.unauthenticatedClientAction in authsettingsV2.
    /// Valid values: RedirectToLoginPage, AllowAnonymous, RejectWith401, RejectWith404.
    /// Default is RedirectToLoginPage (suitable for web UIs); APIs should use RejectWith401.
    /// </summary>
    public string UnauthenticatedClientAction { get; set; } = "RedirectToLoginPage";

    /// <summary>
    /// Paths excluded from EasyAuth authentication (e.g. webhook endpoints).
    /// Applied to globalValidation.excludedPaths in authsettingsV2.
    /// Supports App Service glob patterns (e.g. "/api/Public*").
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>
    /// Identity provider key used for unauthenticated redirect (when UnauthenticatedClientAction
    /// is RedirectToLoginPage). Applied to globalValidation.redirectToProvider in authsettingsV2.
    /// Default "azureactivedirectory" (displayed as "Microsoft" in the Azure portal).
    /// Override only when configuring a non-AAD provider (e.g. "google", "facebook").
    /// </summary>
    public string RedirectToProvider { get; set; } = "azureactivedirectory";

    /// <summary>
    /// Client application IDs allowed to call the app with access tokens.
    /// Applied to identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications.
    /// When empty, no application-level restriction is applied (any valid token for the audience is accepted).
    /// </summary>
    public List<string> AllowedApplications { get; set; } = [];

    /// <summary>
    /// Additional allowed token audiences beyond the auto-generated "api://{appId}".
    /// Applied to identityProviders.azureActiveDirectory.validation.allowedAudiences.
    /// The app's own "api://{appId}" is always included automatically.
    /// </summary>
    public List<string> AllowedAudiences { get; set; } = [];

    /// <summary>
    /// Tenant IDs allowed to authenticate. Controls both the issuer URL and the
    /// WEBSITE_AUTH_AAD_ALLOWED_TENANTS app setting.
    ///
    /// Behavior:
    ///   - Empty (default): single-tenant — issuer is set to the setup tenant ID.
    ///   - One entry: single-tenant — issuer is set to that tenant ID.
    ///   - Multiple entries: issuer is set to "common" and WEBSITE_AUTH_AAD_ALLOWED_TENANTS
    ///     is set to the comma-separated list (Azure enforces the tid claim check).
    ///
    /// The tenant from the setup flow is always included automatically.
    /// </summary>
    public List<string> AllowedTenants { get; set; } = [];

    /// <summary>
    /// When set, the EasyAuth client secret is stored in Azure Key Vault instead of
    /// directly in the app setting. The app setting AUTH_SECRET is then written as a
    /// Key Vault reference (@Microsoft.KeyVault(SecretUri=...)).
    ///
    /// Value is the Key Vault name (e.g. "my-vault" → https://my-vault.vault.azure.net).
    /// If set to the literal string "auto", the site name (WEBSITE_SITE_NAME) is used
    /// as the vault name.
    ///
    /// The managed identity must have Secret Set permission on the vault.
    /// When empty (default), the secret is stored directly in the app setting.
    /// </summary>
    public string KeyVaultName { get; set; } = "";

    /// <summary>
    /// Role(s) assigned to the bootstrap user seeded by /api/setup/seed-user.
    /// When empty (default), the role "superadmin" is used.
    /// Do NOT set defaults here — .NET config binding appends to list initializers, causing duplicates.
    /// Override in appsettings to match the hosted app's role taxonomy, e.g. ["owner"] or ["admin", "authenticated"].
    /// </summary>
    public List<string> FirstUserRoles { get; set; } = [];
}

/// <summary>
/// Container health monitoring — tracks restart count on persistent storage (/home)
/// to detect crash loops and force Azure to provision a new worker instance.
/// </summary>
public class ContainerHealthSettings
{
    /// <summary>
    /// Maximum consecutive restarts of the same instance within the time window
    /// before blocking Kestrel startup. Azure's health probe will then time out,
    /// forcing the platform to reallocate to a new worker.
    /// Set to 0 to disable crash-loop detection.
    /// </summary>
    public int MaxRestarts { get; set; } = 3;

    /// <summary>
    /// Time window in minutes. Only restarts within this window are counted.
    /// Restarts outside the window reset the counter.
    /// </summary>
    public int WindowMinutes { get; set; } = 30;

    /// <summary>
    /// Directory for the restart tracker file. Defaults to /home/craft on Linux
    /// (Azure Files persistent mount). Leave empty to use the platform default.
    /// Set to an explicit path to override, or set MaxRestarts to 0 to disable.
    /// </summary>
    public string TrackerDirectory { get; set; } = "";
}
