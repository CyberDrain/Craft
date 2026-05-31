# ── Craft Dev Image ────────────────────────────────────────
# CyberDrain Runtime for Apps, Functions, Tasks
# Dev build — includes Debug symbols for diagnostics.

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY Craft.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Debug -o /app/publish && \
    rm -rf /app/publish/ref \
           /app/publish/cs /app/publish/de /app/publish/es /app/publish/fr \
           /app/publish/it /app/publish/ja /app/publish/ko /app/publish/pl \
           /app/publish/pt-BR /app/publish/ru /app/publish/tr \
           /app/publish/zh-Hans /app/publish/zh-Hant

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app

# Copy PowerShell from the SDK build stage (PS is bundled in dotnet/sdk images)
COPY --from=build /usr/share/powershell /usr/share/powershell
RUN ln -s /usr/share/powershell/pwsh /usr/bin/pwsh

# Optional SSH server for Azure App Service SCM access. Installed unconditionally
# but NOT started — the entrypoint launches sshd only when CRAFT_SSH_ENABLED=true.
# Also installs libmimalloc; see Dockerfile.release for the full rationale.
RUN apt-get update \
 && apt-get install -y --no-install-recommends openssh-server libmimalloc2.0 \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /run/sshd
COPY docker/sshd_config /etc/ssh/sshd_config
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

COPY --from=build /app/publish .

# ─── Runtime Configuration ────────────────────────────────
ARG APP_VERSION=dev
ARG COMMIT_SHA=unknown
ARG IMAGE_TAG=dev
ENV APP_VERSION=$APP_VERSION
ENV COMMIT_SHA=$COMMIT_SHA
ENV IMAGE_TAG=$IMAGE_TAG

ENV CRAFT_VERBOSE="false"
# SSH is opt-in. Set CRAFT_SSH_ENABLED=true at runtime to start sshd on port 2222.
ENV CRAFT_SSH_ENABLED="false"

# ── Memory & runtime tuning (kept in sync with Dockerfile.release) ──
ENV LD_PRELOAD=/usr/lib/x86_64-linux-gnu/libmimalloc.so.2
ENV MALLOC_ARENA_MAX=2
ENV POWERSHELL_TELEMETRY_OPTOUT=1
ENV POWERSHELL_UPDATECHECK=Off
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# ── .NET GC tuning (kept in sync with Dockerfile.release) ──
ENV DOTNET_gcServer=1
ENV DOTNET_GCDynamicAdaptationMode=1
ENV DOTNET_GCHeapHardLimitPercent=46
ENV DOTNET_GCConserveMemory=7

EXPOSE 8080 2222
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["/entrypoint.sh"]
