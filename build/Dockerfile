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

COPY --from=build /app/publish .

# ─── Runtime Configuration ────────────────────────────────
ARG APP_VERSION=dev
ARG COMMIT_SHA=unknown
ARG IMAGE_TAG=dev
ENV APP_VERSION=$APP_VERSION
ENV COMMIT_SHA=$COMMIT_SHA
ENV IMAGE_TAG=$IMAGE_TAG

ENV CRAFT_VERBOSE="false"
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Craft.dll"]
