namespace CRAFT.Services;

/// <summary>
/// Central configuration for the CRAFT (CyberDrain Runtime for Apps, Functions, Tasks) host.
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

    /// <summary>Script repository — where to find PowerShell modules, HTTP endpoints, background scripts.</summary>
    public ScriptRepoSettings Scripts { get; set; } = new();
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
    /// PowerShell scripts to run once on the first worker for process-level warmup.
    /// Runs sequentially after module import. Errors are non-fatal (logged as warnings).
    /// Example: ["Get-MyAppAuth | Out-Null", "Initialize-ConnectionPool"]
    /// </summary>
    public List<string> WarmupScripts { get; set; } = [];

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

    /// <summary>Azure Table name for user authorization (UPN → roles).</summary>
    public string UserTableName { get; set; } = "allowedUsers";

    /// <summary>
    /// Storage connection string for the allowedUsers table.
    /// If empty, falls back to AzureWebJobsStorage (same storage as the rest of the app).
    /// Set this to isolate the user table in a separate storage account.
    /// </summary>
    public string UserStorageConnection { get; set; } = "";

    /// <summary>
    /// Roles assigned to the dev-mode auto-login principal.
    /// </summary>
    public List<string> DevRoles { get; set; } = ["superadmin", "authenticated", "anonymous"];

    /// <summary>User ID for the dev-mode auto-login principal.</summary>
    public string DevUserId { get; set; } = "00000000-0000-0000-0000-000000000000";

    /// <summary>User details (UPN/email) for the dev-mode auto-login principal.</summary>
    public string DevUserDetails { get; set; } = "developer@localhost";

    /// <summary>
    /// PowerShell function name for the /api/me endpoint.
    /// If empty, /api/me returns the raw client principal without PS processing.
    /// </summary>
    public string MeEndpointFunction { get; set; } = "";
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
