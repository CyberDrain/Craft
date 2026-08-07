# Craft architecture — modular monolith

Craft is a **modular monolith**: one deployable process (`Craft.dll`) with clear module boundaries.
Configuration and the PowerShell contract DTOs are separate projects; feature modules still live in
the host until remaining type edges are inverted.

## Layout

| Path | Role |
|------|------|
| [`src/Craft/`](../src/Craft/) | Web host — Bridges, feature modules, Program, Runtime; publishes `Craft.dll` |
| [`src/Craft.Configuration/`](../src/Craft.Configuration/) | Settings POCOs only (dependency leaf); files grouped by feature area under `Auth/`, `Hosting/`, `PowerShell/`, … — namespace stays `Craft.Configuration` |
| [`src/Craft.Contracts/`](../src/Craft.Contracts/) | Pinned `Craft.Services` DTOs + `HttpResponseContext` |
| [`tests/`](../tests/) | Unit tests — sibling of `src/`, never compiled into `Craft.dll` |
| [`perf-harness/`](../perf-harness/) | E2E / load tooling — not part of the app project |

`Craft.sln` and `Directory.Build.props` stay at the repo root.

## Modules (folders under host `Services/`)

| Folder | Namespace | Responsibility |
|--------|-----------|----------------|
| `Bridges/` | **`Craft.Services` (PINNED)** | Thin PowerShell facades over DI services |
| `PowerShellHost/` | `Craft.PowerShellHost` (+ pinned `PowerShellRunnerService` in `Craft.Services`) | Runspaces, pool, script discovery |
| `Orchestration/` | `Craft.Orchestration` | Jobs, scheduler, Durable-Functions-shaped fan-out, queue ingress |
| `Storage/` | `Craft.Storage` | Azure Tables |
| `Caching/` | `Craft.Caching` | Response cache |
| `Auth/` | `Craft.Auth` | EasyAuth / session |
| `Realtime/` | `Craft.Realtime` | SSE |
| `Setup/` | `Craft.Setup` | First-run wizard + setup-mode session flags |
| `Hosting/` (+ `Endpoints/`) | `Craft.Hosting` | Middleware, DI extensions, HTTP endpoints, metrics/startup trackers |
| `Program.cs` | (top-level) | Composition root — wires modules; owns startup order |

## Dependency rules

```
Program / Hosting.Endpoints / Bridges
        │
        ▼
   feature modules (Auth, Caching, Orchestration, Setup, Realtime, …)
        │
        ▼
   PowerShellHost / Storage
        │
        ▼
   Craft.Contracts  (pinned DTOs)
        │
        ▼
   Craft.Configuration
```

**Bridges** sit on the PowerShell edge only: they call into feature modules after `Initialize`.
Feature modules must **not** call static bridges for core logic — they use DI services instead:

| Concern | Domain owner | PowerShell facade |
|---------|--------------|-------------------|
| Setup-mode flags | `SetupSessionState` | `AppLifecycleBridge` |
| Startup progress | `StartupProgressService` | `StartupInfoBridge` |
| Worker metrics | `WorkerMetricsService` | `WorkerMetricsBridge` |
| Orchestration ingress/drain | `OrchestratorService` | `OrchestratorBridge` |
| Queue ingress/drain | `QueueDispatchService` | `QueueBridge` |

Composition-root `*.Initialize` calls in `Program.cs` (and `LogBridge.Initialize` during logging setup)
are the host edge — not domain cycles.

## PowerShell public contract (do not break)

Downstream apps resolve types by **fully-qualified name**. These must remain public under
`Craft.Services` (or the Functions mirror namespace for `HttpResponseContext`):

- All types listed in [`PowerShellContractTests`](../tests/Craft.Tests/PowerShellContractTests.cs)
- Bridge facades live in the **host** assembly; DTOs live in **Craft.Contracts** (same namespace)
- `PowerShellRunnerService` is pinned to `Craft.Services` and still lives under the host's `PowerShellHost/` folder
- `Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext` keeps that exact type name (in Craft.Contracts)

Folder / assembly moves are fine. **Namespace renames are a coordinated breaking change** with every hosted app.

## Visibility

Host wiring (endpoint mappers, setup middleware, `CraftHostBuilderExtensions`) is `internal`, with
`InternalsVisibleTo("Craft.Tests")` on the host. Host-only mutable state (`WorkerStats`, `JobRecord`,
pending queue records, `CacheEntry`) lives in feature namespaces as `internal`. `Craft.Contracts`
keeps `InternalsVisibleTo("Craft")` for DTO `internal set`ters (e.g. `StartupStats`) mutated by the
host. Types that tests assert on directly stay `public`. Bridge `Initialize` methods used only by
the composition root are `internal`.

## Future project splits

Still blocked on type edges (not call cycles):

- PowerShellHost ↔ Hosting (`OperationContext`, metrics, profilers, `HandlerHeaders`)

When those are inverted, extract Auth / Caching / Orchestration / … and optionally rename the host
folder to `Craft.Host` while keeping `AssemblyName=Craft` so Docker still runs `Craft.dll`.
