# ── Craft Runtime ──────────────────────────────────────────
# CyberDrain Runtime for Apps, Functions, Tasks.
#
# One Azure Linux *distroless* image for both dev and production:
#   - dev builds pass --build-arg BUILD_CONFIGURATION=Debug (default: Release)
#   - distroless = no shell, no package manager, non-root (APP_UID=1654)
#   - no SSH server; layer one on downstream if a deployment needs it
# The SDK / build stage stays on Debian; only the runtime is Azure Linux.
#
# Default the .NET base images to the GHCR mirror (CyberDrain/Containers) so
# builds survive an mcr.microsoft.com / Azure Container Registry outage. Build
# against upstream until the mirror carries the azurelinux tags:
#   docker build -f build/Dockerfile --build-arg DOTNET_REGISTRY=mcr.microsoft.com -t craft:local .
#
# ── Base image patching ──────────────────────────────────────────────────
# The tags below deliberately float on the `8.0-*` minor, so .NET runtime security patches are picked
# up automatically. That only works if the tag is actually re-resolved on each build:
#
#   docker build --pull -f build/Dockerfile -t craft:local .
#
# WITHOUT --pull, Docker reuses whatever `8.0-...` it already has locally, potentially for months. The
# CVEs this matters for live in /usr/share/dotnet/shared/Microsoft.NETCore.App/ — the shared runtime
# shipped by the base image, NOT anything referenced from Craft.csproj. No package bump can fix them
# and `dotnet list package --vulnerable` will never report them; only rebuilding on a newer base does.
# CI passes `pull: true` for the same reason (.github/workflows/*-container.yml).
#
# Do NOT pin these to an exact patch (e.g. 8.0.29) unless you also own the job that bumps the pin —
# a stale pin is strictly worse than a floating tag, because it looks deliberate.
ARG DOTNET_REGISTRY=ghcr.io/cyberdrain

FROM ${DOTNET_REGISTRY}/dotnet/sdk:8.0-bookworm-slim AS build
ARG BUILD_CONFIGURATION=Release
# Supplied automatically by buildx (amd64 / arm64). Mapped to a .NET RID below.
ARG TARGETARCH
WORKDIR /src

# Map the Docker arch to a .NET RID once, and reuse it for both restore and publish so the restore
# layer actually caches. A mismatch here silently costs a full re-restore on every publish.
RUN case "${TARGETARCH:-amd64}" in \
  amd64) echo linux-x64   > /tmp/rid ;; \
  arm64) echo linux-arm64 > /tmp/rid ;; \
  *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
  esac

# Restore inputs only, so the restore layer caches across source-only changes. Directory.Build.props
# and global.json must land BEFORE restore: the props file sets properties restore evaluates, and
# global.json pins the SDK — omit either and restore and publish disagree about what they're building.
COPY Craft.csproj Directory.Build.props global.json ./
RUN dotnet restore -r "$(cat /tmp/rid)"

COPY . .
# Two things are load-bearing here:
#
#   Craft.csproj, named explicitly — with no argument `dotnet publish` resolves Craft.sln, which
#   includes tests/Craft.Tests and publishes xunit, testhost and coverlet into the runtime image.
#
#   -r <rid> --self-contained false — WITHOUT a RuntimeIdentifier, publish keeps the RID-agnostic
#   layout and copies EVERY platform's assets out of the NuGet packages: runtimes/win (17 MB), plus
#   osx, ios, tvos, solaris, illumos, freebsd and the other linux RIDs. That was 21 MB of a 53 MB
#   app layer, none of which a linux/amd64|arm64 image can ever load. Naming the RID makes the SDK
#   resolve one platform's assets and flatten them into the output root.
#   --self-contained false is explicit so this stays framework-dependent: the .NET runtime comes from
#   the base image, which is what lets a base-image refresh patch runtime CVEs (see the note above).
#
# ── DO NOT trim Roslyn or ref/ — `Add-Type -TypeDefinition` depends on BOTH ──────────────
# Runtime C# compilation is a capability a PowerShell host is expected to have, and hosted apps use
# it. It needs two things present, and removing either one breaks it:
#
#   Microsoft.CodeAnalysis.dll + Microsoft.CodeAnalysis.CSharp.dll   (~9 MB, the compiler)
#   ref/                                                             (~2 MB, reference assemblies)
#
# Both are tempting trim targets and both have been measured here. `Add-Type -TypeDefinition` run
# from inside a real CRAFT PowerShell runner:
#
#   ref/ deleted   -> FAIL: DirectoryNotFoundException: '/app/ref'   (compiler present but unusable)
#   ref/ present   -> OK:   compiled and invoked
#
# The failure mode is what makes this worth spelling out: it is not a build error, it surfaces as a
# runtime exception inside a hosted app's script, and dropping only the compiler produces a different
# exception for the same root problem. The satellite locale folders below ARE safe to delete; these
# two are not.
RUN dotnet publish Craft.csproj -c ${BUILD_CONFIGURATION} \
  -r "$(cat /tmp/rid)" --self-contained false \
  -o /app/publish && \
  rm -f /app/publish/Craft.xml && \
  rm -rf \
  /app/publish/cs /app/publish/de /app/publish/es /app/publish/fr \
  /app/publish/it /app/publish/ja /app/publish/ko /app/publish/pl \
  /app/publish/pt-BR /app/publish/ru /app/publish/tr \
  /app/publish/zh-Hans /app/publish/zh-Hant

# mimalloc staged from Debian (glibc) — not reliably packaged in Azure Linux's
# tdnf repos, and glibc→glibc so it loads cleanly on the Azure Linux runtime.
FROM ${DOTNET_REGISTRY}/dotnet/aspnet:8.0-bookworm-slim AS mimalloc-src
ARG TARGETARCH
RUN set -eux; \
  apt-get update && apt-get install -y --no-install-recommends libmimalloc2.0; \
  case "${TARGETARCH}" in \
  amd64) TRIPLET=x86_64-linux-gnu ;; \
  arm64) TRIPLET=aarch64-linux-gnu ;; \
  *) echo "unsupported arch: ${TARGETARCH}" >&2; exit 1 ;; \
  esac; \
  install -D /usr/lib/${TRIPLET}/libmimalloc.so.2 /mimalloc/libmimalloc.so.2

# distroless-extra = distroless + icu/tzdata. PowerShell needs globalization, so
# the plain -distroless tag (no ICU) would break it — the -extra tag is required.
FROM ${DOTNET_REGISTRY}/dotnet/aspnet:8.0-azurelinux3.0-distroless-extra AS runtime
# IMPORTANT: the app lives in /app, NOT under /home. Azure App Service mounts persistent storage over
# /home when WEBSITES_ENABLE_APP_SERVICE_STORAGE=true, which shadows anything the image ships there —
# an app under /home/app loses its binaries at runtime ("Craft.dll does not exist"). So we keep the app
# in /app and make that dir writable by the non-root user via the chowned COPY below (no WORKDIR here —
# see that COPY for why).

# NOTE: the standalone /usr/share/powershell (~50 MB) is intentionally NOT copied.
# CRAFT hosts PowerShell in-process via Microsoft.PowerShell.SDK (engine + built-in
# modules ship in /app/publish). Restore these two lines only if a module spawns an
# external pwsh (Start-Job) or needs a $PSHOME-only module:
#   COPY --from=build /usr/share/powershell /usr/share/powershell
#   ENV PATH="/usr/share/powershell:${PATH}"

COPY --from=mimalloc-src /mimalloc/libmimalloc.so.2 /usr/lib/libmimalloc.so.2

# Create /app owned by the non-root user. COPYing with NO preceding WORKDIR makes Docker CREATE the
# destination /app with this ownership; a `WORKDIR /app` first would pre-create it root-owned, leaving
# the directory node itself unwritable (only its contents chowned) — which is what blocked runtime
# _cache/_data creation. With this, the app user owns the whole /app tree. WORKDIR comes AFTER, when
# /app already exists app-owned.
COPY --chown=$APP_UID:$APP_UID --from=build /app/publish /app
WORKDIR /app

# ─── Runtime Configuration ────────────────────────────────
ARG APP_VERSION=0.0.0-local
ARG COMMIT_SHA=unknown
ARG IMAGE_TAG=latest
ENV APP_VERSION=$APP_VERSION
ENV COMMIT_SHA=$COMMIT_SHA
ENV IMAGE_TAG=$IMAGE_TAG

ENV CRAFT_VERBOSE="false"

# Writable state needs no path envs: /app is app-owned (chowned COPY above), so runtime _cache/_data
# land there fine; logs + restart-tracker default to the app user's home (RuntimePaths in
# CraftSettings.cs). App-owned content such as API/Config/function-permissions.json is writable in
# place — downstream images that COPY additional files as root should still --chown them to APP_UID.

# ── Memory & runtime tuning ──
ENV LD_PRELOAD=/usr/lib/libmimalloc.so.2
ENV MALLOC_ARENA_MAX=2
ENV POWERSHELL_TELEMETRY_OPTOUT=1
ENV POWERSHELL_UPDATECHECK=Off
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# ── .NET GC tuning ──
ENV DOTNET_gcServer=0
ENV DOTNET_GCDynamicAdaptationMode=1
ENV DOTNET_GCHeapHardLimit=0x80000000
ENV DOTNET_GCConserveMemory=7

# App port only. Runs as the image's default non-root user (APP_UID=1654).
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Exec form — no shell is invoked.
ENTRYPOINT ["dotnet", "Craft.dll"]
