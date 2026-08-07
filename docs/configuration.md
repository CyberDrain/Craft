# Craft Configuration Guide

Craft (CyberDrain Runtime for Apps, Functions, Tasks) is configured through ASP.NET Core's standard configuration system. All application-specific settings live under the `"App"` section.

## Where defaults come from

**Craft ships no `appsettings.json`.** Every setting's default is the C# property initialiser in
[`src/Craft.Configuration/CraftSettings.cs`](../src/Craft.Configuration/CraftSettings.cs) — that file is the single source of truth, and a
deployment that sets nothing at all gets exactly those values.

[`appsettings.example.jsonc`](../appsettings.example.jsonc) is an annotated reference listing every key
alongside its default. It is **documentation only**: the `.jsonc` extension keeps it out of the
`appsettings*.json` content glob in `src/Craft/Craft.csproj`, so it is never copied to the published output or the
container image. It carries comments, which strict JSON does not allow — hence the extension.

Copy out of it only the keys you are actually changing. Restating a default in your own config creates a
value that silently goes stale the day the default moves.

## Configuration Hierarchy

Settings are merged in priority order (highest wins):

1. **Environment variables** — `App__Worker__BgPoolSize=8` (containers / CI / production)
2. **`src/Craft/Properties/launchSettings.json`** — profile env vars injected by `dotnet run` / Visual Studio (local only; sets `ASPNETCORE_ENVIRONMENT=Development`)
3. **.NET user secrets** — local `dotnet run` only (Development). Connection strings and other secrets go here — **not** in an `appsettings.json` on disk
4. **`appsettings.{Environment}.json` / `appsettings.json`** — optional non-secret overlays a *downstream app* may ship in its image. Do not use these for local secrets
5. **C# property defaults** in `src/Craft.Configuration/CraftSettings.cs` — the floor; always present

The host registers settings with `AddOptions<CraftSettings>().BindConfiguration("App")`, applies SKU /
`AzureWebJobsStorage` post-configure, and `ValidateOnStart` for readiness mode and pool sizes. Nested
section objects use `= new()` so defaults apply when a section is absent from config.

### Local development (user secrets only)

For `dotnet run`, put every secret and connection string in the user-secrets store. Secrets never leave your
machine and are never a file in the repo (gitignored or otherwise).

```bash
# from the repo root (src/Craft/Craft.csproj has UserSecretsId=CyberDrain.Craft)
# Azurite: Development already allows the emulator fallback — only set a connection when you need a
# real storage account (or want to be explicit):
dotnet user-secrets set "AzureWebJobsStorage" "UseDevelopmentStorage=true" --project src/Craft/Craft.csproj
# Optional App-section secrets use ":" (JSON shape), not "__":
dotnet user-secrets set "App:Auth:UserStorageConnection" "UseDevelopmentStorage=true" --project src/Craft/Craft.csproj
# EasyAuth client secret — only needed when exercising real AAD login / setup flows locally:
dotnet user-secrets set "AUTH_SECRET" "local-dev-only-change-me" --project src/Craft/Craft.csproj
dotnet user-secrets list --project src/Craft/Craft.csproj
dotnet run --project src/Craft/Craft.csproj
```

`WebApplication.CreateBuilder` loads user secrets only when the environment is **Development**. The checked-in
[`src/Craft/Properties/launchSettings.json`](../src/Craft/Properties/launchSettings.json) sets that for the default `dotnet run`
profile. If you override the environment to Production locally, secrets will not load — and storage will
fail closed unless `AzureWebJobsStorage` / `App:Storage:ConnectionString` is set another way.

With `ASPNETCORE_ENVIRONMENT=Development` and no connection string at all, Craft already falls back to
`UseDevelopmentStorage=true` (Azurite). User secrets are for when you need a real account or other secrets.

Containers and App Service keep using environment variables / Key Vault references — user secrets are a
local-dev mechanism, not a deployment one.

Environment variables use `__` (double underscore) as the section separator:

```
App__Worker__BgPoolSize=8
App__Auth__CookieName=my-session
App__Scheduler__CheckIntervalSeconds=60
```

In Docker Compose:

```yaml
environment:
  - App__Worker__BgPoolSize=8
  - App__Worker__HttpPoolSize=3
  - CRAFT_LOG_LEVEL=Debug
```

## Full Settings Reference

### Top Level

```jsonc
{
  "App": {
    "Name": "MyApp",  // Display name used in logs and diagnostics

    // Controls when Kestrel starts accepting connections:
    // "Immediate" — Kestrel starts first, init runs in background (default, fast startup)
    // "HttpReady"  — Kestrel starts after HTTP worker pool is ready
    // "AllReady"   — Kestrel starts after all worker pools are fully initialized
    "ReadinessMode": "Immediate",

    // Kestrel request timeout (seconds). Controls how long Kestrel waits for a complete
    // request (headers + body) before aborting. Does NOT control PowerShell execution time.
    // If not set (or 0): derives from Worker.HttpTimeoutSeconds if > 0, else no timeout.
    // Recommended: set slightly higher than HttpTimeoutSeconds to give workers time to respond.
    // Example: HttpTimeoutSeconds=120, KestrelTimeoutSeconds=130
    "KestrelTimeoutSeconds": 0
  }
}
```

---

### Roles (split deployments)

Craft ships as **one image** that can run in split roles selected at runtime, so the cheap-to-scale static
frontend and the compute-heavy PowerShell backend can be deployed and scaled independently.

Three independent capabilities:

| Capability | Serves | Env flag | Config |
|---|---|---|---|
| **Frontend** | static web content from `Frontend/` | `CRAFT_SERVE_FRONTEND` | `App:Roles:Frontend` |
| **Http** | `/api` + auth (`/login`, `/.auth/*`, `/api/me`) via the HTTP pool | `CRAFT_SERVE_API` | `App:Roles:Http` |
| **Background** | scheduler / orchestrator / job-manager / stats via the BG pool | `CRAFT_RUN_BACKGROUND` | `App:Roles:Background` |

**Resolution (highest wins):**
1. If **any** role is explicitly set (env `CRAFT_SERVE_*`/`CRAFT_RUN_*` wins over `App:Roles:*`) → the host
   uses exactly those; unset roles default **off**. (So you declare roles by enabling what you want.)
2. Else (nothing set) → **all three on** — the default monolith.

If all three resolve off, the host fails fast (`EX_CONFIG`). The resolved set is logged at startup:
`[System] Roles: Frontend=on Http=off Background=off | ResponseCache=off Compression=on`.

**Presets** (combinations that fall out of the flags):

| Preset | Frontend | Http | Background | Use |
|---|:-:|:-:|:-:|---|
| `frontend` | ✓ | – | – | Pure static host (CDN origin), no PowerShell |
| `http` | – | ✓ | – | API-only node (does **not** process orchestrations — see caveat) |
| `background` | – | – | ✓ | Worker node — scheduler + orchestrator processing |
| `backend` | – | ✓ | ✓ | Self-contained API + workers, no frontend |
| `frontend+http` | ✓ | ✓ | – | App node without background workers |
| `combined` *(default)* | ✓ | ✓ | ✓ | The monolith |

A node without the **Http** role maps none of the API/auth handlers, so `/api`, `/.auth`, `/login` fall
through to static serving first (a **Frontend** node can expose e.g. `/api/me` from its own static dir) and
finally to `MapFallback` (which `404`s unmatched `/api`/`/.auth`). A node without the **Frontend** role serves
no static content and `404`s SPA routes.

A role-agnostic **health endpoint** is available in every mode (200 while the process is up; body reports
per-role readiness) — point Azure/K8s liveness at it. It defaults to `/healthz` and can be relocated or
disabled: `App:Health:Path` / `CRAFT_HEALTH_PATH` (e.g. `/status`) and `App:Health:Enabled` /
`CRAFT_HEALTH_ENABLED=false`.

> **⚠️ Orchestration caveat.** Triggering an orchestration and processing it must happen in the **same
> process** — the trigger is an in-process queue, not a durable cross-process one. A pure `http` node that
> triggers orchestrations will not run them. Any node that triggers orchestrations from HTTP must also carry
> the **Background** role: use **`backend`** or **`combined`**. A pure `background` node self-triggers via its
> own scheduler and processes in-process, which is fine.

```jsonc
"Roles": {
  "Frontend": true,   // null/unset = not explicitly set
  "Http": true,
  "Background": false
}
```

Docker Compose (env flags, same image):

```yaml
# frontend node (static origin behind a CDN)
environment: [ CRAFT_SERVE_FRONTEND=true ]

# api node (API only — no orchestration processing)
environment: [ CRAFT_SERVE_API=true, WEBSITE_AUTH_CLIENT_ID=..., AUTH_SECRET=... ]

# worker node (scheduler + orchestrator processing)
environment: [ CRAFT_RUN_BACKGROUND=true, AzureWebJobsStorage=... ]

# backend node (API + workers, no frontend) — self-contained backend
environment: [ CRAFT_SERVE_API=true, CRAFT_RUN_BACKGROUND=true, WEBSITE_AUTH_CLIENT_ID=..., AUTH_SECRET=..., AzureWebJobsStorage=... ]

# monolith (unchanged default) — no role flags
```

**Response cache & roles:** the API response cache (see [Cache](#cache)) defaults **on only when a node
serves both a browser UI and its API** (`combined` / `frontend+http`) and **off** otherwise. Override per node
with `App:Cache:Enabled` (bool) or `CRAFT_RESPONSE_CACHE=true/false`.

---

### Worker

Controls the PowerShell runspace pools that execute all scripts.

```jsonc
"Worker": {
  // Number of dedicated HTTP request workers. Each handles one request at a time.
  // Increase if you see HTTP requests queuing during concurrent load.
  // 0 = build no HTTP runspaces — for apps whose routes are all native C# endpoints.
  "HttpPoolSize": 2,

  // Number of background workers for scheduler, orchestrator, and queue tasks.
  // Higher = more parallel orchestrator tasks, but more memory.
  // 0 = build no BG runspaces — for apps whose scheduled work is all native C# tasks (the
  // scheduler and JobManager still run; native tasks execute on the .NET thread pool).
  "BgPoolSize": 4,

  // Run each worker's PowerShell pipeline on one reused thread instead of a new thread per invocation.
  // Default true — the biggest single per-request dispatch win (~50% of the PS-invoke cost, +68% throughput
  // on dispatch-bound load; see ../perf-harness/dispatch-analysis.md), and matches the Azure Functions PS worker's
  // persistent runspace. Safe: each worker owns one runspace and serves one request at a time. Set false
  // only to A/B or if a module misbehaves on a long-lived pipeline thread.
  "ReuseRunspaceThread": true,

  // Maximum execution time (seconds) for HTTP request handlers.
  // When exceeded, the PowerShell pipeline is stopped and the worker is reclaimed.
  // 0 = no timeout (default). Recommended: 120-300 for HTTP endpoints.
  "HttpTimeoutSeconds": 0,

  // Maximum execution time (seconds) for background jobs (scheduler, orchestrator tasks).
  // When exceeded, the PowerShell pipeline is stopped and the worker is reclaimed.
  // 0 = no timeout (default). Recommended: 600-3600 for background jobs.
  "BgTimeoutSeconds": 0,

  // Extra env vars injected into every runspace.
  // Use "{ApiBasePath}" as a placeholder — replaced with the resolved API directory.
  "EnvVars": {
    "CIPPNG": "true",
    "MyCustomVar": "value"
  },

  // Additional env var names set to the API root path (alongside $env:CRAFT_ROOT).
  // Lets existing scripts use their own root variable without code changes.
  "RootPathVars": ["CIPPRootPath", "CIPPRoot"],

  // Scripts run once on the first worker for process-level warmup.
  // Errors are non-fatal (logged as warnings). Runs after module import.
  "WarmupScripts": [
    "Get-MyAuth | Out-Null",
    "$null = Get-Tenants -IncludeErrors"
  ],

  // .NET assemblies to load into each runspace (paths relative to API/).
  "SharedAssemblies": [
    "Shared/CIPPSharp/bin/CIPPSharp.dll"
  ],

  // Inject shared Synchronized Hashtables into module scopes.
  // Enables cross-runspace state sharing (e.g. token caches).
  "ModuleInjections": [
    {
      "Module": "CIPPCore",       // Target module name
      "Variable": "classictoken", // $script:classictoken inside the module
      "CacheKey": "ClassicTokenCache"  // Shared key — same key = same hashtable
    }
  ],

  // Scripts run after init on EVERY worker (not just the first).
  "PostInitScripts": [],

  // Module directory names to skip during import.
  // Use for test modules or legacy entrypoints you don't want loaded.
  "SkipModules": ["CippEntrypoints"],

  // JSON files to preload into PowerShell variables at startup.
  "JsonPreloads": [
    {
      "File": "Config/function-permissions.json",  // Relative to API/
      "Variable": "CIPPFunctionPermissions",       // Variable name (no $ prefix)
      "Scope": "global",    // "global" → $global:Var, "env" → $env:Var (raw JSON string)
      "AsHashtable": true   // true → case-insensitive Hashtable, false → PSObject
    }
  ]
}
```

**Memory impact:** Each worker consumes ~100–200 MB depending on module count. A config with `HttpPoolSize: 2` + `BgPoolSize: 4` uses ~6 workers × 150 MB ≈ 900 MB baseline. Scale accordingly.

**Startup behavior:** The first HTTP worker initializes sequentially and runs `WarmupScripts` (which set process-level state like auth tokens and caches). All remaining workers (HTTP + BG) initialize **in parallel** afterward, benefiting from the process-level state already set. With source (non-compiled) modules, parallel init significantly reduces startup time.

---

### Auth

Controls authentication and the dev-mode auto-login principal.

```jsonc
"Auth": {
  // Cookie name for authenticated sessions
  "CookieName": "craft-session",

  // Azure Table used for user → role mappings
  "UserTableName": "allowedUsers",

  // Storage connection for the allowedUsers table.
  // If empty, uses AzureWebJobsStorage (same storage as the rest of the app).
  // Set this to point to a separate storage account for isolation.
  "UserStorageConnection": "",

  // Roles assigned to the auto-login principal in Development mode.
  // Production uses real Azure AD auth — this is dev-only.
  "DevRoles": ["superadmin", "admin", "editor", "readonly", "authenticated", "anonymous"],

  // User ID GUID for the dev principal
  "DevUserId": "00000000-0000-0000-0000-000000000000",

  // UPN/email for the dev principal
  "DevUserDetails": "developer@localhost",

  // PowerShell function dispatched for /api/me.
  // Empty string = use the literal "me" as the endpoint name.
  // The PS function (or its MeEndpointHandler wrapper) owns the response shape —
  // /api/me passes status code and body through unchanged.
  "MeEndpointFunction": "me",

  // Optional wrapper PS function invoked for /api/me instead of MeEndpointFunction directly.
  // When set, the handler receives the standard Request/TriggerMetadata parameters and is
  // expected to dispatch internally based on Request.Params.CIPPEndpoint (which is set to
  // MeEndpointFunction). When empty (default), MeEndpointFunction is invoked directly.
  // Example (CIPP): "New-CippCoreRequest"
  "MeEndpointHandler": "",

  // When true (default), any user who authenticates against the configured Azure AD tenant
  // is allowed in — even if they're not in the allowedUsers table.
  // Users not in the table get ["authenticated", "anonymous"] as default roles.
  // The hosted app can then do its own role resolution (e.g. via Entra group mapping).
  // When false, only users explicitly listed in the allowedUsers table can log in.
  // Override via env var: App__Auth__AllowAllTenantUsers=false
  "AllowAllTenantUsers": true
}
```

---

### Scheduler

Drives the cron-based background task system.

```jsonc
"Scheduler": {
  // JSON file containing task definitions (looked up in API/Config/ then API/).
  // Must be a JSON array of task objects with Id, Command, Cron, Priority, etc.
  "ConfigFile": "CIPPTimers.json",

  // How often (seconds) the scheduler checks for due tasks.
  "CheckIntervalSeconds": 30
}
```

**Task file format** (e.g. `CIPPTimers.json`):

```json
[
  {
    "Id": "unique-guid",
    "Command": "Start-MyOrchestrator",
    "Description": "Nightly data collection",
    "Cron": "0 0 3 * * *",
    "Priority": 10,
    "RunOnProcessor": true,
    "IsSystem": true
  }
]
```

- **Cron** — 6-field format (seconds included): `sec min hour day month weekday`
- **Priority** — Lower number = higher priority in the job queue
- **IsSystem** — System tasks can't be disabled from the UI

**How `Command` resolves.** Native-first: if a scanned assembly ships a
`[CraftScheduledTask("<Command>")]` class (see [Native C# endpoints](#native-c-endpoints-and-scheduled-tasks)),
the timer fires that; otherwise the PowerShell script table is consulted, exactly as before. A name
present in both worlds fires the native task and logs the shadowed PowerShell function — same rule,
same visibility, as route collisions. `IsOrchestratorOverride` is not supported on native commands
(the planner/task split is a PowerShell construct; native code does its own fan-out) and is rejected
at load with an error naming the timer.

---

### Background concurrency limiter

Gates how many background/orchestrator tasks run at once, on top of the `Worker.BgPoolSize` runspaces.
Bound under `App:BackgroundLimiter` (env: `App__BackgroundLimiter__*`). Legacy root-level keys
(`BackgroundBaseConcurrency`, etc.) are still accepted via post-bind for older harness compose files.

By default it starts narrow and ramps slowly, to keep idle memory low; tune it for bursty fan-out.

| Key | Default | Effect |
|---|---|---|
| `App:BackgroundLimiter:BaseConcurrency` | `clamp(cores, 2, 4)` | starting width when idle |
| `App:BackgroundLimiter:ScaleUpAfterSeconds` | `15` | how long the queue must be backed up before ramping (doubles per 10s tick) |
| `App:BackgroundLimiter:MaxConcurrency` | `BgPoolSize` | ceiling |
| `App:BackgroundLimiter:BurstToCeiling` | `false` | jump straight to the ceiling the moment tasks queue, skipping the ramp — **~2.7× faster fan-out** for bursts shorter than the ramp dwell (see ../perf-harness/orch-analysis.md) |
| `App:BackgroundLimiter:OverSubscribe` | `0` | admit this many tasks *above* the ceiling so they can do their pre-invoke table write and queue at the worker checkout while the pool stays full (helps only up to Azure Table write throughput) |
| `App:BackgroundLimiter:HttpPressureThreshold` | `HttpPoolSize/2` | busy-HTTP-worker count that throttles BG to 2; `0` disables |
| `App:BackgroundLimiter:HttpPressureAfterSeconds` | `10` | how long HTTP pressure must persist before throttling |

```jsonc
"BackgroundLimiter": {
  "BurstToCeiling": true,     // fill the pool immediately on a fan-out burst
  "ScaleUpAfterSeconds": 5    // or: ramp sooner without going straight to ceiling
}
```

---

### Orchestrator

Fan-out/fan-in task execution with crash recovery.

```jsonc
"Orchestrator": {
  // Prefix for Azure Tables: {Prefix}Runs, {Prefix}Tasks, {Prefix}Results
  "TablePrefix": "Orchestrator",

  // Batch + coalesce per-task/run STATUS writes off the fan-out critical path, in ≤100-entity byte-budgeted
  // Azure Table transactions. Default true. This is the throughput fix for large fan-outs — the per-task
  // table write was the ceiling (see ../perf-harness/orch-analysis.md). Results are NEVER batched (their chunking /
  // multi-row large-payload path is untouched). Set false to fall back to per-task writes.
  "BatchStatusWrites": true,
  // Write the pre-invoke "Running" marker under a durable barrier (persisted BEFORE the task runs, batched
  // with concurrently-starting tasks) so AttemptCount/MaxRetries still bounds poison tasks. Default true.
  // False = eventual: the marker rides the periodic flush and the task doesn't wait — max throughput (100%
  // pool utilization) at the cost of the strict poison-before-invoke guarantee. Terminal + run states stay
  // durable in both modes (flushed before a run finalizes and on shutdown).
  "DurableRunningBarrier": true,
  // Status-writer flush interval / barrier latency ceiling (ms). Default 25.
  "StatusFlushIntervalMs": 25,

  // PS function that executes individual tasks. Receives TaskJson parameter.
  // Default: "Invoke-CraftTask" (provided in CraftRuntime/)
  "GenericTaskFunction": "Invoke-CraftTask",

  // PS function for queue-triggered commands. Receives Cmdlet + ParametersJson.
  // Default: "Invoke-CraftQueueTask" (provided in CraftRuntime/)
  "QueueTaskFunction": "Invoke-CraftQueueTask",

  // PS function for post-execution aggregation. Receives FunctionName + ResultsJson.
  // Default: "Invoke-CraftPostExecution" (provided in CraftRuntime/)
  "PostExecFunction": "Invoke-CraftPostExecution",

  // Max task interruptions (host crash/restart) before marking Failed.
  "MaxRetries": 3
}
```

All three function settings have sensible defaults provided by the `CraftRuntime/` scripts. Most apps only need to set `TablePrefix`.

**Queuing orchestrator runs from PowerShell:**

Call `Start-CraftOrchestrator` (provided in `CraftRuntime/`) to queue a fan-out run:

```powershell
Start-CraftOrchestrator -InputObject @{
    OrchestratorName = 'MyDataCollection'
    Batch = @(
        @{ FunctionName = 'CollectData'; TenantFilter = 'contoso.com' }
        @{ FunctionName = 'CollectData'; TenantFilter = 'fabrikam.com' }
    )
    PostExecution = @{ FunctionName = 'AggregateResults' }
}
```

This bridges into the C# `OrchestratorService` via `OrchestratorBridge`. Applications can provide their own wrapper function (e.g. CIPP uses `Start-CIPPOrchestrator` for dual-boot compatibility) — just have it call `[Craft.Services.OrchestratorBridge]::QueueOrchestration()` internally.

**How orchestration works:**
1. A scheduler task or HTTP endpoint calls `Start-CraftOrchestrator` with a batch
2. The PS bridge queues it to the C# `OrchestratorService` via `OrchestratorBridge`
3. Tasks are dispatched through the `JobManager` with priority ordering
4. State is persisted to Azure Table Storage after every change
5. On restart, interrupted tasks resume automatically
6. After all tasks finish, optional `PostExecution` aggregates results and can start a second phase

---

### Cache

In-memory index + disk-backed (`_cache/`) response cache for HTTP `List*` GET endpoints.

```jsonc
"Cache": {
  // Whether the cache is active. Omit (default) for auto: ON only when this node serves BOTH a browser UI
  // and its API (combined / frontend+http roles), OFF for api-only, worker-only and static-only nodes.
  // Set true/false to force it. Env override (wins): CRAFT_RESPONSE_CACHE=true/false.
  // When disabled, no _cache/ directory is created or scanned and all get/set operations are no-ops.
  // "Enabled": true,

  // Bytes budget for the in-memory body tier (LRU over the disk cache) — a HIT returns the body from RAM
  // instead of re-reading + re-decoding the file. Default 64 MiB; 0 = disk-only. Gain scales with response
  // size (+44% throughput for small List* responses, +157% at ~150KB; see ../perf-harness/cache-analysis.md).
  "MaxMemoryBytes": 67108864,

  // Maximum cached responses held in memory
  "MaxEntries": 1000,

  // Default TTL (seconds) for cached responses
  "DefaultTtlSeconds": 600,

  // Query parameter that triggers cache invalidation when "true"
  "InvalidateParam": "InvalidateCIPPCache",

  // Query parameter for scoped invalidation (e.g. per-tenant).
  // When a write clears cache, only entries with matching scope value are evicted.
  "ScopeParam": "tenantFilter",

  // Endpoints never cached, whatever the query string says.
  // Case-insensitive; "*" matches any run of characters.
  "ExcludedEndpoints": ["ListLogs", "ListScheduled*"],

  // Query parameter a request must carry before its response may be cached at all.
  // Empty (default) = every eligible read is cached.
  "RequiredParam": "tenantFilter",

  // Values of RequiredParam that skip the cache (case-insensitive).
  "ExcludedParamValues": ["AllTenants"],

  // Request header that bypasses the cache for a single call. Empty disables the check.
  "NoCacheHeader": "x-craft-no-cache",

  // Per-endpoint TTL overrides (seconds). Key = endpoint name.
  "EndpointTtl": {
    "ListTenants": 300,
    "ListUsers": 120
  }
}
```

#### What gets cached

A response is cached only when **both** gates agree:

1. **The handler** is a side-effect-free read — a `GET` to a `List*` endpoint. This is the naming
   convention, and it is not configurable.
2. **The request** passes the admission policy below.

The policy is evaluated in this order, and the first failure wins:

| Gate | `X-Cache-Bypass` when it fails |
| --- | --- |
| Endpoint matches `ExcludedEndpoints` | `excluded-endpoint` |
| `NoCacheHeader` sent with any value other than `false`/`0`/`no` | `no-cache-header` |
| `RequiredParam` missing from the query string | `missing-required-param` |
| `RequiredParam` present but blank | `empty-required-param` |
| `RequiredParam` value listed in `ExcludedParamValues` | `excluded-param-value` |

A bypassed request neither reads from nor writes to the cache, so it can never collide with an entry
stored by a differently-scoped caller, and it answers with `X-Cache: BYPASS`. `X-Cache: MISS` still
means what it always did — the request was eligible and there was simply no entry for it.

All of these are inert by default (`RequiredParam` empty, no excluded values, no excluded endpoints),
so an existing deployment that upgrades keeps caching exactly what it cached before until it opts in.

**`ExcludedEndpoints` vs `RequiredParam`.** They cover different cases and are worth using together.
`RequiredParam` classifies in bulk: everything that does not take the scoping parameter drops out
without anyone having to enumerate it. `ExcludedEndpoints` handles the exceptions that rule cannot see
— an endpoint that *does* take `tenantFilter` and is still a poor cache candidate, because it is
cheap, near-realtime, or answered per user rather than per tenant. Patterns accept `*` anywhere
(`ListLog*`, `*Logs`, `List*Audit*`), matched case-insensitively against the endpoint name as it
appears in the route.

**Why require a parameter at all.** `List*` endpoints that take no scope parameter — a tenant list, a
log tail, the scheduler view — are usually fast, query-shaped and answered per user. Caching them buys
little and invites key collisions between users whose results legitimately differ. Requiring
`tenantFilter` keeps the cache to the calls where it pays for itself, and, because every cached key
then contains `tenantFilter=…`, it also makes `ScopeParam` invalidation exact.

Per-call bypass:

```bash
curl -H "x-craft-no-cache: true" https://example/API/ListUsers?tenantFilter=contoso.com
```

---

### Scripts

Controls where the host discovers PowerShell scripts.

```jsonc
"Scripts": {
  // Module directories (under API/Modules/) scanned for HTTP endpoint functions.
  // All HTTP-category functions become /api/{route} endpoints.
  // If a function starts with "Invoke-", that prefix is stripped for the route.
  "HttpModules": ["CIPPHTTP"],

  // Optional global HTTP handler. When set, ALL /API/{endpoint} routes dispatch through
  // this PS function instead of invoking Invoke-{endpoint} directly. The endpoint name
  // is passed via Request.Params.CIPPEndpoint so the handler can dispatch internally.
  // Use when the hosted app expects all routes to go through a common router
  // (e.g. CIPP's New-CippCoreRequest which performs Test-CIPPAccess + telemetry).
  "HttpHandler": "New-CippCoreRequest",

  // Directories (under API/) scanned for background scripts.
  // These are deployed as Function:\ items on each worker.
  "BackgroundScriptDirs": [],

  // RBAC metadata extraction from comment-based help (.ROLE, .FUNCTIONALITY)
  "PermissionExtraction": {
    "Enabled": true,
    "Modules": ["CIPPHTTP"],
    "OutputFile": "Config/function-permissions.json"
  }
}
```

### Native C# endpoints and scheduled tasks

HTTP endpoints and scheduled tasks written in C#, hosted alongside the PowerShell ones. Off unless
you name the assemblies to scan; costs nothing when off.

```jsonc
"Endpoints": {
  "Enabled": true,

  // Absolute, or relative to the API base path.
  "Assemblies": ["bin/MyApp.dll"],

  // What to do when a native endpoint claims a route a PowerShell function already has:
  // PreferNative (default) | PreferPowerShell (instant rollback) | Fail (right for CI).
  "OnCollision": "PreferNative",

  // Refuse to start when Central-dispatch endpoints exist but no ICraftEndpointHandler was found.
  // Default false. Set true (in CI at minimum) when authorization lives in the central handler.
  "RequireHandler": false,

  // Blanket in-flight limit for endpoints that declare none, and per-route overrides.
  // With HttpPoolSize=0 nothing else caps concurrent work — see the comments in EndpointSettings.cs.
  "MaxConcurrency": 0,
  "Concurrency": { "GeoDBDownload": 4 },
  "QueueTimeoutSeconds": 0
}
```

The assembly scan discovers four things:

| Contract | Purpose |
|---|---|
| `ICraftEndpoint` + `[CraftEndpoint("Route")]` | An HTTP endpoint at `/API/{Route}` |
| `ICraftEndpointHandler` | THE central entrypoint — at most one per app |
| `ICraftScheduledTask` + `[CraftScheduledTask("Command")]` | A scheduled task fired from the timer file |
| `ICraftServiceModule` | DI registrations for an app with no `Program.cs` of its own |

**The central handler.** The native counterpart of `Scripts:HttpHandler`: one place every
authenticated API call funnels through (resolve principal → check role/plan → invoke, or refuse).
It wraps the endpoint (`HandleAsync(request, invokeEndpoint, ct)`), so it can short-circuit,
pass through, or post-process. Endpoints opt out per-route in code:

```csharp
[CraftEndpoint("StripeWebhook", Dispatch = EndpointDispatch.Direct)]   // signature IS the auth
[CraftEndpoint("QrRedirect", Dispatch = EndpointDispatch.Direct)]      // deliberately anonymous
[CraftEndpoint("EditLink", Role = "qr.edit")]                          // Central (default) — handler
                                                                       // reads Role off request.Endpoint
```

`Dispatch` defaults to `Central`, so a route that never thought about it gets the application's
authorization rather than becoming accidentally public. Direct routes are listed in the startup log —
that line is the security-review checklist. Two handlers fail startup (which one ran would be
assembly scan order); zero is legal and means every endpoint dispatches direct, exactly the
pre-handler behaviour. `ICraftEndpointFilter` still runs before *every* endpoint including Direct
ones — filters are for telemetry and throttling that public routes must not escape; the handler is
for authorization, which is precisely what a public route opts out of.

**Native scheduled tasks.** Fired by the scheduler from the same timer file as PowerShell commands
(`Command` resolves native-first — see [Scheduler](#scheduler)), enqueued through the same
`JobManager`, so priority ordering, job records and the background concurrency limiter apply
unchanged. They run on the .NET thread pool: no runspace is involved, which is what makes the
all-native configuration real —

```
Worker:HttpPoolSize=0   no HTTP runspaces (all routes native)
Worker:BgPoolSize=0     no BG runspaces  (all scheduled work native)
```

With both at 0 the container hosts no PowerShell at all and startup logs
`No PowerShell pools — native endpoints/tasks only`. One semantic difference to design for:
`Worker:BgTimeoutSeconds` stops a PS pipeline forcibly, but a native task is cancelled
*cooperatively* — pass the token to everything that accepts one, because a task that ignores it
runs on.

---

### Realtime (SSE)

Identity-gated Server-Sent Events channel at `/.craft/events`, fed in-process by
`[Craft.Services.RealtimeBridge]::Publish(...)` from PowerShell.

**Off by default — opt in.** Set `Enabled: true` (or `CRAFT_REALTIME_ENABLED=true`, which wins over config).
While off the endpoint is not mapped, `Publish` calls are no-ops, and no state or timer is held. When on, the
endpoint is still only mapped by nodes carrying the **Http** or **Frontend** role.

```jsonc
"Realtime": {
  "Enabled": false,           // opt-in switch — set true to serve /.craft/events

  // Tuning (defaults shown)
  "MaxMessageBytes": 16384,   // per-event data cap; over this the payload is dropped and a 413 frame is sent
  "MaxActiveJobs": 10000,     // max stored (userId, jobId) entries
  "MaxConnections": 1000,     // max concurrent SSE streams
  "PerConnectionQueue": 256,  // buffered frames per connection before the oldest is dropped
  "HeartbeatSeconds": 20,     // keep-alive comment interval
  "EntryTtlMinutes": 60       // backstop eviction for jobs that never send "end"
}
```

### Frontend

EasyAuth handles auth, redirects, and excluded paths at the App Service platform layer (see `Setup.UnauthenticatedClientAction` and `Setup.ExcludedPaths`). CRAFT only adds response headers EasyAuth doesn't touch — currently just CSP.

```jsonc
"Frontend": {
  // Content-Security-Policy applied to all responses. Null/empty = no CSP set.
  "ContentSecurityPolicy": "default-src 'self' https: blob: 'unsafe-eval' 'unsafe-inline'; connect-src 'self' https: blob: data:; object-src 'self' blob:; img-src 'self' blob: data: *"
}
```

If you override this, keep `'self'` in both `default-src` and `connect-src`. A policy that only lists the `https:` scheme blocks the app's own same-origin `fetch` calls whenever it is reached over http — behind a TLS-terminating proxy, self-hosted, or in local docker. `'self'` permits exactly one origin, so it does not widen a policy that already allows every `https:` host.

Keep `data:` in `connect-src` as well. Emscripten `SINGLE_FILE` builds inline their WebAssembly as a `data:application/octet-stream;base64,…` URL and then `fetch` it, which `connect-src` gates — wasm-backed layout and parsing libraries are commonly shipped this way. Such loaders normally fall back to decoding the base64 in JavaScript, so blocking it costs a failed request and a console error rather than breaking the feature, but there is no reason to pay for it. It belongs in `connect-src` and not in `default-src`, which would also hand `data:` to `script-src`.

---

## Environment Variables

These are process-level variables read directly (not part of `App:*`):

| Variable | Default | Description |
|----------|---------|-------------|
| `CRAFT_LOG_LEVEL` | `Information` | Minimum log level for file/console output and PowerShell stream capture. Values: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`. At `Debug`, Write-Debug is captured; at `Trace`, Write-Verbose is also captured. Overrides `App:FileLogging:LogLevel` in appsettings. |
| `CRAFT_ROOT` | *(auto-set)* | API base path — set automatically, available as `$env:CRAFT_ROOT` in PS |
| `CRAFT_DEV_FRONTEND_URL` | `http://localhost:3000` | Dev mode: proxy frontend requests to this URL (hot-reload) |
| `CRAFT_REALTIME_ENABLED` | *(unset)* | `true`/`false` — realtime SSE channel at `/.craft/events`. Overrides `App:Realtime:Enabled` (which defaults to **off**) |
| `AzureWebJobsStorage` | *(required)* | Azure Storage connection string for Table Storage |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables dev auth, dev errors, frontend proxy |

### Authentication Environment Variables

These configure the built-in Azure AD / Entra ID OIDC authentication. When set, Craft handles login, token validation, session cookies, and user authorization via the `allowedUsers` Azure Table.

| Variable | Required | Description |
|----------|----------|-------------|
| `WEBSITE_AUTH_CLIENT_ID` | Yes (prod) | Azure AD app registration client/application ID |
| `AUTH_SECRET` | Yes (prod) | Azure AD client secret for the app registration |
| `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` | No | Tenant ID to restrict logins to. Defaults to `common` (any tenant) |

When **none** of these are set, Craft's auth is unconfigured — the `/login` endpoint returns an error and API requests receive no identity header. In **Development** mode, a dev principal is injected automatically (see `Auth.DevRoles`).

These variable names are intentionally compatible with Azure App Service's built-in authentication headers so that the same configuration works whether auth is handled by Craft directly or by the App Service platform.

#### Key Vault References

In production on Azure App Service, **do not put secrets in plain-text App Settings**. Use Key Vault references instead:

```
WEBSITE_AUTH_CLIENT_ID              = @Microsoft.KeyVault(VaultName=myvault;SecretName=ApplicationID)
AUTH_SECRET                         = @Microsoft.KeyVault(VaultName=myvault;SecretName=ApplicationSecret)
WEBSITE_AUTH_AAD_ALLOWED_TENANTS    = @Microsoft.KeyVault(VaultName=myvault;SecretName=TenantID)
```

Key Vault references require:
1. The App Service has a **System-Assigned Managed Identity** enabled
2. The Key Vault has an **access policy** granting that identity `Get` permission on secrets
3. The App Setting values use the `@Microsoft.KeyVault(...)` syntax exactly as shown

App Service resolves these at startup and injects the secret values as environment variables. Craft reads them the same way regardless of whether they are plain values or KV references — it's transparent.

> **Note:** Key Vault access in Craft uses **access policies**, not Azure RBAC for Key Vault. Ensure the Key Vault has access policies enabled (the default), not "Azure role-based access control" as the permission model.

#### allowedUsers Table

The `allowedUsers` table works the same way as Azure Static Web Apps user invitations — it's an application-level authorization layer that maps Azure AD identities to app-specific roles. This is separate from Azure AD group membership or app roles; it gives the application full control over who can access it and with what permissions.

By default, the table lives in the same storage account as the rest of the app (`AzureWebJobsStorage`). To isolate it — for example, to share a single user table across multiple Craft instances, or to keep user data in a separate storage account from operational data — set `Auth.UserStorageConnection` to a different connection string.

Locally (user secrets):
```bash
dotnet user-secrets set "App:Auth:UserStorageConnection" "UseDevelopmentStorage=true"
```

In a downstream non-secret `appsettings.json` (prefer Key Vault / env for real credentials):
```jsonc
"Auth": {
  "UserStorageConnection": "DefaultEndpointsProtocol=https;AccountName=myuserstorage;AccountKey=..."
}
```

Or via App Settings with a Key Vault reference:
```
App__Auth__UserStorageConnection = @Microsoft.KeyVault(VaultName=myvault;SecretName=UserStorageConnection)
```

#### How Authentication Works

**Login flow:**
1. User visits `/login` (or `/.auth/login/aad`) → redirected to Azure AD
2. Azure AD authenticates user → redirects to `/.auth/callback` with authorization code
3. Craft exchanges the code for tokens, validates the `id_token` JWT signature against Azure AD's published signing keys
4. User's UPN is checked against the `allowedUsers` Azure Table for role mapping (or assigned default roles if `AllowAllTenantUsers` is enabled)
5. An encrypted session cookie is set (AES-256, derived from `AUTH_SECRET`)
6. The `x-ms-client-principal` header is injected on subsequent requests (SWA-compatible format)

**Session details:**
- Cookie name is configurable via `Auth.CookieName` (default: `craft-session`)
- Sessions are stored in-memory with an 8-hour TTL (token expiry + 1 hour grace)
- Cookie is `HttpOnly`, `Secure`, `SameSite=Lax`

**Auth header formats — Craft handles three scenarios:**

| Source | Detection | Behavior |
|--------|-----------|----------|
| **App Service EasyAuth** | `x-ms-client-principal` with `claims` array, no `userRoles` | Transforms to SWA format, adds roles from `allowedUsers` table |
| **Azure SWA** | `x-ms-client-principal` with `userRoles` array | Passes through as-is |
| **Craft session cookie** | No `x-ms-client-principal` header, valid session cookie | Builds and injects the header from the validated session |

This means Craft works identically whether deployed standalone (container), behind Azure App Service authentication, or behind Azure Static Web Apps — downstream PowerShell always sees the same `x-ms-client-principal` header format.

#### allowedUsers Table Schema

The `allowedUsers` Azure Table (name configurable via `Auth.UserTableName`) maps user identities to roles:

| Column | Value |
|--------|-------|
| `PartitionKey` | Any partition value (e.g. `"User"`) |
| `RowKey` | User's UPN / email (e.g. `admin@contoso.com`) |
| `Roles` | JSON array of role strings: `["admin", "editor"]` |

Roles are cached in-memory for 5 minutes. If a user is not in the table, behavior depends on `Auth.AllowAllTenantUsers`:

- **`true` (default):** User is allowed in with default roles `["authenticated", "anonymous"]`. The hosted application can perform its own role resolution (e.g. CIPP maps Entra group membership to CIPP roles via `/api/me`). Users explicitly listed in the table still get their table-defined roles.
- **`false`:** User is denied access (401). Only users explicitly listed in the allowedUsers table can log in.

#### Redirect URIs

When using Craft's built-in auth, the Azure AD app registration must include these redirect URIs:

```
https://<your-host>/.auth/callback
```

If your application also uses OAuth flows (e.g. SAM token refresh), add those redirect URIs to the same app registration as needed.

---

## Example: CIPP Configuration

The CIPP application's `appsettings.Development.json` shows a full real-world configuration:

```jsonc
{
  "App": {
    "Name": "CIPP",

    "Worker": {
      "HttpPoolSize": 2,
      "BgPoolSize": 4,
      "RootPathVars": ["CIPPRootPath", "CIPPRoot"],
      "EnvVars": { "CIPPNG": "true" },
      "WarmupScripts": [
        "if (-not $env:ApplicationID) { Get-CIPPAuthentication | Out-Null }",
        "$null = Get-Tenants -IncludeErrors"
      ],
      "SharedAssemblies": ["Shared/CIPPSharp/bin/CIPPSharp.dll"],
      "ModuleInjections": [
        { "Module": "CIPPCore", "Variable": "classictoken", "CacheKey": "ClassicTokenCache" }
      ],
      "SkipModules": ["CippEntrypoints"],
      "JsonPreloads": [
        { "File": "Config/function-permissions.json", "Variable": "CIPPFunctionPermissions", "Scope": "global", "AsHashtable": true },
        { "File": "Config/cipp-roles.json", "Variable": "CIPPBaseRoles", "Scope": "global" }
      ]
    },

    "Auth": {
      "CookieName": "cipp-session",
      "DevRoles": ["superadmin", "admin", "editor", "readonly", "authenticated", "anonymous"],
      "MeEndpointFunction": "me",
      "MeEndpointHandler": "New-CippCoreRequest",
      "AllowAllTenantUsers": true
    },

    "Scheduler": { "ConfigFile": "CIPPTimers.json" },

    "Orchestrator": {
      "TablePrefix": "CippOrchestrator"
    },

    "Cache": {
      "InvalidateParam": "InvalidateCIPPCache",
      "ScopeParam": "tenantFilter",
      "RequiredParam": "tenantFilter",
      "ExcludedParamValues": ["AllTenants"],
      "ExcludedEndpoints": ["ListLogs", "ListScheduledItems"]
    },

    "Scripts": {
      "HttpModules": ["CIPPHTTP"],
      "BackgroundScriptDirs": [],
      "PermissionExtraction": {
        "Enabled": true,
        "Modules": ["CIPPHTTP"],
        "OutputFile": "Config/function-permissions.json"
      }
    }
  }
}
```

---

## Directory Structure

Craft separates its own built-in scripts from application content:

```
/app/
├── Runtime/              ← Craft built-in (ships with the base image)
│   ├── CraftRuntime/     ← Orchestrator/queue/task scripts
│   └── HTTP/Exec/        ← Built-in admin endpoint
├── API/                  ← Application content (downstream overlay)
│   ├── Modules/          ← PowerShell modules
│   ├── Config/           ← App config files (timers, permissions, etc.)
│   └── Shared/           ← Shared assemblies
└── Frontend/             ← Static frontend build
```

`Runtime/` is owned by Craft and always loaded. `API/` is 100% owned by the downstream application — safe to volume-mount in dev without overwriting Craft internals.

---

## Quick Start: Onboarding a New Application

1. **Place compiled PS modules** in `API/Modules/`
2. **Place frontend build** in `Frontend/` (static files served automatically)
3. **Configure `App:` settings** — `App__*` environment variables for containers; for local `dotnet run`, use `dotnet user-secrets` for secrets/connection strings (see [Local development](#local-development-user-secrets-only)). Non-secret structural defaults may live in a downstream `appsettings.json`. Use [`appsettings.example.jsonc`](../appsettings.example.jsonc) as the key reference; only set what you're changing.
4. **Set storage** — locally, Development already allows Azurite (`UseDevelopmentStorage=true`); put a real connection string in user secrets as `AzureWebJobsStorage` when needed. Deployed environments use env / Key Vault.
5. **Run:** `dotnet run` or `docker compose up`

The host auto-discovers modules, builds route tables from HTTP endpoint functions, starts the scheduler, and serves both API and frontend from a single process.
