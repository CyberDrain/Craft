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
    /// JSON files to preload into PowerShell variables at worker init.
    /// Each entry specifies a file path (relative to API/Config/), a variable name,
    /// and a scope ("global" or "env"). This replaces the need for hardcoded
    /// permission/role loading — any JSON file can be injected generically.
    /// </summary>
    public List<GlobalJsonPreload> JsonPreloads { get; set; } = [];
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
    /// PowerShell function name for the /api/me endpoint.
    /// If empty, /api/me returns the raw client principal without PS processing.
    /// </summary>
    public string MeEndpointFunction { get; set; } = "";

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
    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// Include the logger category name in log output.
    /// When true:  "2026-05-13 10:30:00.000 [INF] [Microsoft.AspNetCore.Routing] Matched endpoint"
    /// When false: "2026-05-13 10:30:00.000 [INF] Matched endpoint"
    /// </summary>
    public bool IncludeCategory { get; set; } = false;

    /// <summary>Resolved directory path, applying platform defaults when Directory is empty.</summary>
    internal string ResolvedDirectory => !string.IsNullOrEmpty(Directory)
        ? Directory
        : OperatingSystem.IsLinux() ? "/logs" : Path.Combine(AppContext.BaseDirectory, "logs");
}

/// <summary>
/// Response cache settings.
/// </summary>
public class CacheSettings
{
    /// <summary>Maximum number of cached responses in memory.</summary>
    public int MaxEntries { get; set; } = 1000;

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
    /// When true, the setup wizard activates automatically if EasyAuth is not configured.
    /// When false, the child app must explicitly call
    /// [Craft.Services.AppLifecycleBridge]::RequestSetupMode() to activate setup mode.
    /// This lets the child app decide when setup is appropriate (e.g. after checking
    /// for existing credentials that can be migrated automatically).
    /// </summary>
    public bool AutoActivate { get; set; } = true;

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
}
