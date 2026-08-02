# Craft

**C**yberDrain **R**untime for **A**pps, **F**unctions, **T**asks

Craft is a lightweight ASP.NET Core runtime that hosts PowerShell modules as HTTP endpoints, background workers, orchestrators, and scheduled tasks. It replaces Azure Functions for containerized deployments.

## Structure

```
Craft/
├── Services/
│   ├── Program.cs               # Host startup, middleware, endpoint mapping
│   ├── Bridges/                 # PowerShell-facing API surface  → namespace Craft.Services (PINNED)
│   ├── Configuration/           # Settings types, one per file   → Craft.Configuration
│   ├── Endpoints/               # Native C# endpoint/task contracts → Craft.Endpoints
│   ├── PowerShellHost/          # Runspace workers, pool, script repo → Craft.PowerShellHost
│   ├── Orchestration/           # Orchestrator, scheduler, jobs  → Craft.Orchestration
│   ├── Storage/                 # Azure Table stores, health     → Craft.Storage
│   ├── Caching/                 # Response cache                 → Craft.Caching
│   ├── Hosting/                 # Logging, diagnostics, profilers → Craft.Hosting
│   ├── Auth/                    # EasyAuth translation           → Craft.Auth
│   ├── Realtime/                # SSE channel                    → Craft.Realtime
│   └── Setup/                   # First-run wizard + its HTML    → Craft.Setup
├── Runtime/                     # PowerShell runtime bridge
├── Properties/                  # ASP.NET launch profiles
├── build/
│   ├── Dockerfile               # Container image build
│   └── config/                  # Runtime config templates
├── docs/                        # Documentation
├── appsettings.example.jsonc    # Annotated config reference (NOT loaded — see Configuration)
├── .editorconfig                # Code style, enforced in CI by `dotnet format`
├── Directory.Build.props        # Shared build + analyzer settings
├── global.json                  # Pinned SDK (keep in sync with CI and the Dockerfile)
└── Craft.csproj                 # Project file
```

## Quick Start

```bash
dotnet run
```

```bash
docker build --pull -f build/Dockerfile -t craft .
```

Use `--pull`. The base image tags float on the `8.0-*` minor so .NET runtime security patches are
picked up automatically, but only if the tag is re-resolved — without it Docker will happily reuse a
months-old local base image. Those CVEs live in the shared runtime the base image ships, so no package
bump fixes them and `dotnet list package --vulnerable` will never report them. CI sets `pull: true`.

## Configuration

Craft ships **no `appsettings.json`**. Every setting's default is the C# property initialiser in
[`Services/CraftSettings.cs`](Services/CraftSettings.cs), which is the single source of truth. Configure a
deployment with `App__*` environment variables, or by supplying your own `appsettings.json` (downstream app
image, or the project root for local `dotnet run`).

[`appsettings.example.jsonc`](appsettings.example.jsonc) is an annotated reference listing every key and its
default. It is documentation only — the `.jsonc` extension keeps it out of the build glob, and reflects that
it contains comments and so is not strict JSON.

See [docs/configuration.md](docs/configuration.md) for the full reference.

## The `Craft.Services` namespace is a public contract

Everything in [`Services/Bridges/`](Services/Bridges), plus `PowerShellRunnerService`, stays in namespace
`Craft.Services` **permanently**. Downstream PowerShell reaches these types by fully-qualified name:

```powershell
[Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
[Craft.Services.OrchestratorBridge]::QueueOrchestration(...)
```

Renaming that namespace compiles cleanly and then fails at runtime inside the hosted app with
*"Unable to find type"*. Type forwarding cannot rescue it — `[TypeForwardedTo]` only works across
assemblies, and these all live in `Craft.dll`. The folder a pinned type lives in is free to change; the
namespace is not. Each pinned file carries a `NAMESPACE PINNED` header saying so.

Same rule, different reason, for `Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext`: its
namespace must match the real Azure Functions worker type because hosted-app routers match on
`PSObject.TypeNames`.

All four of its properties reach the wire — `StatusCode`, `Body`, `Headers` and `ContentType` — so a
handler can redirect:

```powershell
return [HttpResponseContext]@{
    StatusCode = [HttpStatusCode]::Found
    Headers    = @{ Location = $Url }
}
```

Header values are normalised by `Craft.Hosting.HandlerHeaders` before they are written: CR/LF is
stripped, empty values are dropped rather than emitted as empty headers, and the headers Kestrel
computes for itself (`Content-Length`, `Transfer-Encoding`, `Connection`, `Host`, …) are refused.
Headers are cached and replayed alongside the status and body, so a cached redirect still redirects.

## Tests

```bash
dotnet test Craft.sln -c Release
```

Unit tests live in [`tests/Craft.Tests`](tests/Craft.Tests) and run in CI ahead of the E2E suite. Two
suites carry most of the weight today:

- **`PowerShellContractTests`** — asserts every `[Craft.Services.*]` type PowerShell calls by name still
  exists, is public, and that nothing unrelated has drifted into that frozen namespace. This is the only
  thing that turns a downstream-breaking rename into a build failure instead of a runtime one.
- **`ConfigurationReferenceTests`** — asserts every default documented in `appsettings.example.jsonc`
  still matches the C# default it claims to document, so the example can't rot into a lie.

The end-to-end suite (Azurite + orchestrator + scheduler + realtime + API dispatch) needs Docker:

```bash
docker build -f build/Dockerfile -t craft:ci . && perf-harness/scripts/run-e2e.ps1 -SutImage craft:ci
```

## Contributing

Style is defined by [`.editorconfig`](.editorconfig) and enforced in CI. Before pushing:

```bash
dotnet format Craft.sln --severity warn
```

The build runs with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, and every analyzer rule
enforced at `warning` — there is no backlog held at `suggestion`. If a new rule starts firing, fix it or
set that rule's severity explicitly in `.editorconfig` with a reason; don't disable the switch.

Two justified `CA1051` suppressions exist at their declaration sites (`WorkerStats`, `CacheEntry`) — both
hold `volatile` fields or `Interlocked` targets, neither of which is expressible as a property.

One structural gotcha: `Craft.csproj` sits at the repo root, so its default `**/*.cs` glob reaches the
whole tree. Any new sibling project directory must be added to the `Compile Remove` list in
`Craft.csproj`, or its sources get compiled into `Craft.dll`.
