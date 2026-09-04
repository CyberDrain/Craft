using System.Reflection;
using System.Text;
using System.Text.Json;
using Craft.Configuration;
using Craft.Endpoints;
using Craft.Hosting;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Storage;
using Microsoft.Extensions.Options;

namespace Craft.Telemetry;

/// <summary>
/// Fires ONCE per process start: after readiness and a jittered delay, POSTs a small usage
/// "boot inventory" to a reporting ingest, storm-guarded so a crash loop cannot flood it.
///
/// <para>
/// Runtime-level, so every Craft-based app reports with zero app work. It must never affect the host:
/// every failure path logs at debug/info and swallows. The storm guard is persisted in table storage
/// (outside the container, so it survives crash loops) and is fail-closed — if the guard state cannot
/// be read, nothing is sent.
/// </para>
/// </summary>
internal sealed class StartupTelemetryService(
    IOptions<CraftSettings> settings,
    ICraftTableStore store,
    StorageHealthMonitor storageHealth,
    ScriptRepository scriptRepo,
    SchedulerService scheduler,
    NativeEndpointCatalog nativeCatalog,
    CraftRoles roles,
    IHttpClientFactory httpFactory,
    IHostApplicationLifetime lifetime,
    ILogger<StartupTelemetryService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private const string GuardPartition = "guard";
    private const string ColInstanceId = "InstanceId";
    private const string ColLastSent = "LastSentUtc";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the delay elapsed — the next boot is the retry.
        }
        catch (Exception ex)
        {
            // The emitter must never take the host down. Swallow everything.
            logger.LogDebug(ex, "[Telemetry] Startup emitter failed; swallowed");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var t = settings.Value.Telemetry;

        if (OptOut())
        {
            logger.LogInformation("[Telemetry] CRAFT_TELEMETRY_OPTOUT is set — not sending");
            return;
        }
        if (!t.Enabled)
        {
            logger.LogDebug("[Telemetry] Disabled (App:Telemetry:Enabled=false)");
            return;
        }

        var appId = t.AppId?.Trim();
        if (string.IsNullOrEmpty(appId) || string.IsNullOrWhiteSpace(t.Endpoint))
        {
            logger.LogInformation("[Telemetry] Enabled but AppId/Endpoint unset — not sending");
            return;
        }

        // The guard lives in storage, which a frontend-only node never resolves.
        if (!(roles.Http || roles.Background))
        {
            logger.LogDebug("[Telemetry] Node carries no storage role — not sending");
            return;
        }

        await WaitForStartedAsync(ct);
        if (!await storageHealth.WaitUntilReadyAsync(TimeSpan.FromSeconds(60), ct))
        {
            logger.LogInformation("[Telemetry] Storage not ready — fail-closed, not sending");
            return;
        }

        // Read or mint the storm-guard row. Fail-closed: never emit unguarded.
        var guardKey = TableKeys.Sanitize($"{appId}:{SiteName()}");
        GuardState guard;
        try
        {
            guard = await ReadOrMintGuardAsync(t.GuardTable, guardKey, ct);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "[Telemetry] Guard state unreadable — fail-closed, not sending");
            return;
        }

        // Storm guard: at most one report per MinIntervalHours per instance.
        var minInterval = TimeSpan.FromHours(Math.Max(1, t.MinIntervalHours));
        if (guard.LastSentUtc is { } last && DateTimeOffset.UtcNow - last < minInterval)
        {
            logger.LogDebug("[Telemetry] Within the min-interval window — skipping this boot");
            return;
        }

        // Jittered, deterministic-per-instance delay. A fast crash loop dies inside this window and
        // never reaches the send; it also keeps telemetry out of the cold-start window on Basic SKUs.
        await Task.Delay(JitterDelay(guard.InstanceId, t), ct);

        var report = BuildReport(appId, guard.InstanceId);
        if (await PostAsync(report, t, ct))
        {
            // lastSentUtc advances ONLY on a successful send, so a failed send never silences the instance.
            await WriteGuardAsync(t.GuardTable, guardKey, guard.InstanceId, DateTimeOffset.UtcNow, ct);
            logger.LogInformation("[Telemetry] Startup report sent for {AppId}", appId);
        }
    }

    private async Task WaitForStartedAsync(CancellationToken ct)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedReg = lifetime.ApplicationStarted.Register(() => tcs.TrySetResult());
        using var ctReg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    private async Task<GuardState> ReadOrMintGuardAsync(string table, string key, CancellationToken ct)
    {
        await store.EnsureTableAsync(table, ct);

        var row = await store.GetAsync(table, GuardPartition, key, ct);
        if (row is not null && row.GetString(ColInstanceId) is { Length: > 0 } existing)
            return new GuardState(existing, row.GetDateTimeOffset(ColLastSent));

        // First ever run for this key: mint a stable instance id and persist it (no lastSent yet).
        var minted = Guid.NewGuid().ToString();
        await WriteGuardAsync(table, key, minted, lastSent: null, ct);
        return new GuardState(minted, null);
    }

    private async Task WriteGuardAsync(
        string table, string key, string instanceId, DateTimeOffset? lastSent, CancellationToken ct)
    {
        var row = new StoreRow(GuardPartition, key);
        row[ColInstanceId] = instanceId;
        if (lastSent is { } value) row[ColLastSent] = value;
        await store.UpsertAsync(table, row, ct);
    }

    private StartupReport BuildReport(string appId, string instanceId)
    {
        var roleNames = new List<string>(3);
        if (roles.Frontend) roleNames.Add("frontend");
        if (roles.Http) roleNames.Add("api");
        if (roles.Background) roleNames.Add("background");

        // Counts only — no route names, no per-route hits.
        var routeCount = nativeCatalog.Endpoints.Count + scriptRepo.HttpRoutes.Count;
        var taskCount = scheduler.Tasks.Count;

        return new StartupReport(
            SchemaVersion: 1,
            ReportType: "startup",
            ReportId: Guid.NewGuid().ToString(),
            InstanceId: instanceId,
            SentUtc: DateTimeOffset.UtcNow,
            App: new AppInfo(appId, Env("APP_VERSION"), Env("COMMIT_SHA"), Env("IMAGE_TAG")),
            Craft: new CraftInfo(CraftVersion()),
            Host: new HostInfo(Env("WEBSITE_SKU"), roleNames.ToArray(), Platform(), Region: null),
            Surface: new SurfaceInfo(routeCount, taskCount));
    }

    private async Task<bool> PostAsync(StartupReport report, TelemetrySettings t, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("craft-telemetry");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, t.TimeoutSeconds)));

            var json = JsonSerializer.Serialize(report, s_json);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, t.Endpoint) { Content = content };
            if (!string.IsNullOrEmpty(t.Token))
                request.Headers.TryAddWithoutValidation("X-Telemetry-Token", t.Token);

            using var response = await client.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode) return true;

            logger.LogDebug("[Telemetry] Ingest returned HTTP {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Telemetry] POST failed; the next boot is the retry");
            return false;
        }
    }

    private static TimeSpan JitterDelay(string instanceId, TelemetrySettings t)
    {
        var min = Math.Max(0, t.MinStartupDelaySeconds);
        var max = Math.Max(min, t.MaxStartupDelaySeconds);
        var span = (uint)(max - min) + 1;
        var seconds = min + (int)((uint)StableHash(instanceId) % span);
        return TimeSpan.FromSeconds(seconds);
    }

    // string.GetHashCode is randomized per process, so roll a stable one for a deterministic spread.
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value) hash = (hash * 31) + c;
            return hash;
        }
    }

    private static string? CraftVersion() =>
        typeof(StartupTelemetryService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static string Platform() => Env("WEBSITE_SITE_NAME") is not null ? "appservice" : "container";

    private static string SiteName() => Env("WEBSITE_SITE_NAME") ?? "self";

    private static bool OptOut()
    {
        var value = Environment.GetEnvironmentVariable("CRAFT_TELEMETRY_OPTOUT");
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private sealed record GuardState(string InstanceId, DateTimeOffset? LastSentUtc);
}
