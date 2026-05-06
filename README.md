# Craft

**C**yberDrain **R**untime for **A**pps, **F**unctions, **T**asks

Craft is a lightweight ASP.NET Core runtime that hosts PowerShell modules as HTTP endpoints, background workers, orchestrators, and scheduled tasks. It replaces Azure Functions for containerized deployments.

## Structure

```
Craft/
├── Services/           # C# runtime (workers, scheduler, auth, cache, etc.)
├── Runtime/            # PowerShell runtime bridge
├── Properties/         # ASP.NET launch profiles
├── build/
│   ├── Dockerfile      # Container image build
│   └── config/         # Runtime config templates
├── docs/               # Documentation
├── appsettings.json    # Default configuration
└── Craft.csproj        # Project file
```

## Quick Start

```bash
# Local development
dotnet run

# Container build
docker build -f build/Dockerfile -t craft .
```

See [docs/configuration.md](docs/configuration.md) for full configuration reference.
