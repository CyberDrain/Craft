# ── CRAFT Base Image ───────────────────────────────────────
# CyberDrain Runtime for Apps, Functions, Tasks
# This is the base runtime — app-specific modules, config,
# and frontend are layered on top by downstream projects.

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY CRAFT.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app

# Install PowerShell 7.4 from tar.gz (smaller than apt-based install)
ENV POWERSHELL_VERSION=7.4.7
RUN apt-get update && \
    apt-get install -y --no-install-recommends wget ca-certificates libicu72 && \
    wget -q "https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/powershell-${POWERSHELL_VERSION}-linux-x64.tar.gz" \
         -O /tmp/powershell.tar.gz && \
    mkdir -p /opt/microsoft/powershell/7 && \
    tar -xzf /tmp/powershell.tar.gz -C /opt/microsoft/powershell/7 && \
    chmod +x /opt/microsoft/powershell/7/pwsh && \
    ln -s /opt/microsoft/powershell/7/pwsh /usr/bin/pwsh && \
    rm /tmp/powershell.tar.gz && \
    apt-get remove -y wget && \
    apt-get autoremove -y && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# ─── Runtime Configuration ────────────────────────────────
# All settings are provided at runtime via environment variables
# or appsettings.json overlays. No secrets are baked into the image.

ENV CRAFT_VERBOSE="false"
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "CRAFT.dll"]
