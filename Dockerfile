# ── Craft Base Image ───────────────────────────────────────
# CyberDrain Runtime for Apps, Functions, Tasks
# This is the base runtime — app-specific modules, config,
# and frontend are layered on top by downstream projects.

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY Craft.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish && \
    # Keep only Linux runtimes (x64 + arm64), remove Windows/macOS/ref/localization
    cd /app/publish/runtimes && ls | grep -v 'linux' | xargs rm -rf && \
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

COPY --from=build /app/publish .

# ─── Runtime Configuration ────────────────────────────────
# All settings are provided at runtime via environment variables
# or appsettings.json overlays. No secrets are baked into the image.

ENV CRAFT_VERBOSE="false"
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Craft.dll"]
