namespace Craft.Configuration;

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
    public bool IgnoreSkuProfiles { get; set; }

    /// <summary>
    /// Maximum execution time in seconds for a single HTTP request handler.
    /// When exceeded, the PowerShell pipeline is stopped and the worker is reclaimed.
    /// 0 = no timeout (default). Recommended: 120-300 for HTTP endpoints.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; }

    /// <summary>
    /// Maximum execution time in seconds for a single background job (scheduler, orchestrator task).
    /// When exceeded, the PowerShell pipeline is stopped and the worker is reclaimed.
    /// 0 = no timeout (default). Recommended: 600-3600 for background jobs.
    /// </summary>
    public int BgTimeoutSeconds { get; set; }

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
    public int RecycleAfterInvocations { get; set; }

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
