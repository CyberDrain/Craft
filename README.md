# Craft

**C**yberDrain **R**untime for **A**pps, **F**unctions, **T**asks

Craft is a lightweight ASP.NET Core runtime that hosts PowerShell modules as HTTP endpoints, background workers, orchestrators, and scheduled tasks. It replaces Azure Functions for containerized deployments.

## Structure

```
Craft/
├── src/Craft/                       # Host project (publishes Craft.dll)
│   ├── Craft.csproj
│   ├── Properties/                  # ASP.NET launch profiles (Development + user secrets)
│   ├── Runtime/                     # PowerShell runtime bridge scripts
│   └── Services/                    # Feature modules + Bridges (composition root in Program.cs)
│       ├── Endpoints/               # Native C# endpoint/task contracts → Craft.Endpoints
│       ├── Hosting/                 # Middleware, diagnostics, HTTP endpoint maps
│       ├── PowerShellHost/          # Runspace workers, pool, script repo
│       ├── Orchestration/           # Orchestrator, scheduler, jobs
│       └── Bridges/                 # PowerShell-facing API surface (namespace PINNED)
├── src/Craft.Configuration/         # Settings POCOs (leaf; folders by feature area)
├── src/Craft.Contracts/             # Pinned Craft.Services DTOs + HttpResponseContext
├── tests/Craft.Tests/
├── perf-harness/                    # Docker/k6/E2E tooling (not part of Craft.dll)
├── build/Dockerfile
├── docs/                            # configuration.md, architecture.md, …
├── appsettings.example.jsonc        # Annotated config reference (NOT loaded)
├── Directory.Build.props            # Shared build + analyzer settings
├── global.json                      # Pinned SDK
└── Craft.sln
```

See [docs/architecture.md](docs/architecture.md) for module boundaries and the PowerShell contract rules.

## Quick Start

```bash
dotnet run --project src/Craft/Craft.csproj
```

```bash
docker build --pull -f build/Dockerfile -t craft .
```

Use `--pull`. The base image tags float on the `10.0-*` minor so .NET runtime security patches are
picked up automatically, but only if the tag is re-resolved — without it Docker will happily reuse a
months-old local base image. Those CVEs live in the shared runtime the base image ships, so no package
bump fixes them and `dotnet list package --vulnerable` will never report them. CI sets `pull: true`.

## Configuration

Craft ships **no `appsettings.json`**. Every setting's default is the C# property initialiser in
[`src/Craft.Configuration/CraftSettings.cs`](src/Craft.Configuration/CraftSettings.cs), which is the single source of truth.

- **Local `dotnet run`:** put secrets and connection strings in [.NET user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) only — not in an `appsettings.json` on disk. [`src/Craft/Properties/launchSettings.json`](src/Craft/Properties/launchSettings.json) sets `ASPNETCORE_ENVIRONMENT=Development` so CreateBuilder loads them.
- **Containers / production:** `App__*` (and other) environment variables.
- **Downstream app images:** may COPY a non-secret `appsettings.json` for structural defaults; still prefer env/Key Vault for credentials.

[`appsettings.example.jsonc`](appsettings.example.jsonc) is an annotated reference listing every key and its
default. It is documentation only — the `.jsonc` extension keeps it out of the build glob, and reflects that
it contains comments and so is not strict JSON.

See [docs/configuration.md](docs/configuration.md) for the full reference, including local user-secrets examples.

## The `Craft.Services` namespace is a public contract

Bridge facades under [`src/Craft/Services/Bridges/`](src/Craft/Services/Bridges), `PowerShellRunnerService`,
and the DTOs in [`src/Craft.Contracts/`](src/Craft.Contracts) stay in namespace `Craft.Services`
**permanently** (Facades/runner ship in `Craft.dll`; DTOs in `Craft.Contracts.dll`). Downstream
PowerShell reaches these types by fully-qualified name:

```powershell
[Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
[Craft.Services.OrchestratorBridge]::QueueOrchestration(...)
```

Renaming that namespace compiles cleanly and then fails at runtime inside the hosted app with
*"Unable to find type"*. The folder or assembly a pinned type lives in is free to change; the
namespace is not. Each pinned file carries a `NAMESPACE PINNED` header saying so.

Same rule, different reason, for `Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext`
(in Craft.Contracts): its namespace must match the real Azure Functions worker type because hosted-app
routers match on `PSObject.TypeNames`.

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

The end-to-end suite (Azurite + orchestrator + scheduler + realtime + API dispatch) needs Docker and
PowerShell 7 (`pwsh`):

```bash
# Build + run (from repo root). -Build pulls .NET 10 bases from MCR and tags craft:ci.
pwsh perf-harness/scripts/run-e2e.ps1 -Build

# Or reuse an already-built image:
docker build --pull -f build/Dockerfile -t craft:ci .
pwsh perf-harness/scripts/run-e2e.ps1 -SutImage craft:ci
```

On Apple Silicon / arm64 WSL the image is native arm64 — compose does not pin `platform`. To force
amd64 emulation, build with `docker build --platform linux/amd64 …` and add `platform: linux/amd64`
under the `sut` service in `perf-harness/docker-compose.e2e-azure.yml`.

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
