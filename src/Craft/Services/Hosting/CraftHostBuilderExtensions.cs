using System.Globalization;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Craft.Auth;
using Craft.Caching;
using Craft.Configuration;
using Craft.Endpoints;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Realtime;
using Craft.Services;
using Craft.Setup;
using Craft.Storage;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Craft.Hosting;

/// <summary>
/// Host wiring, split out of <c>Program.cs</c> so startup reads as a short sequence of named steps
/// rather than several hundred lines of inline configuration.
/// </summary>
internal static class CraftHostBuilderExtensions
{
    /// <summary>
    /// Resolves the Kestrel request timeout in seconds: an explicit <c>KestrelTimeoutSeconds</c> wins,
    /// otherwise it derives from <c>Worker.HttpTimeoutSeconds</c>, otherwise 600s.
    /// </summary>
    /// <remarks>
    /// Deriving from the worker timeout matters: if Kestrel gives up before the PowerShell worker does,
    /// the caller sees a connection abort while the script keeps running and holding a runspace.
    /// </remarks>
    public static int ResolveKestrelTimeoutSeconds(CraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var timeout = settings.KestrelTimeoutSeconds;
        if (timeout > 0) return timeout;

        return settings.Worker.HttpTimeoutSeconds > 0 ? settings.Worker.HttpTimeoutSeconds : 600;
    }

    /// <summary>
    /// Resolves the .NET thread-pool minimum: an explicit <c>Worker:MinThreads</c> (or
    /// <c>CRAFT_MIN_THREADS</c>) wins, otherwise it is derived from the worker pools.
    /// </summary>
    /// <remarks>
    /// The derived floor is <c>HttpPoolSize + BgPoolSize + 16</c>, never below the old
    /// <c>max(ProcessorCount * 4, 32)</c>. PowerShell blocks a thread for every outbound call, so a
    /// pool larger than the minimum pays a one-thread-per-second injection ramp on every restart.
    /// </remarks>
    public static int ResolveMinThreads(CraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (int.TryParse(Environment.GetEnvironmentVariable("CRAFT_MIN_THREADS"), out var fromEnv) && fromEnv > 0)
            return fromEnv;

        if (settings.Worker.MinThreads > 0) return settings.Worker.MinThreads;

        var baseline = Math.Max(Environment.ProcessorCount * 4, 32);
        var forPools = settings.Worker.HttpPoolSize + settings.Worker.BgPoolSize + 16;
        return Math.Max(baseline, forPools);
    }

    /// <summary>
    /// Kestrel limits from the Options-bound <see cref="CraftSettings"/> (no pre-Build dual bind).
    /// Also applies <see cref="ResolveMinThreads"/> from configuration (must run before Build).
    /// </summary>
    public static WebApplicationBuilder ConfigureCraftKestrel(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Thread-pool minimum must be set before traffic; bind App:Worker early for this only.
        var early = new CraftSettings();
        builder.Configuration.GetSection("App").Bind(early);
        var minThreads = ResolveMinThreads(early);
        ThreadPool.SetMinThreads(minThreads, minThreads);

        builder.Services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<CraftSettings>>((options, craft) =>
            {
                var settings = craft.Value;
                var timeout = ResolveKestrelTimeoutSeconds(settings);

                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(timeout);
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(Math.Min(60, timeout));

                options.Limits.Http2.MaxStreamsPerConnection = 100;
                options.Limits.Http2.HeaderTableSize = 4096;
                options.Limits.Http2.MaxFrameSize = 16384;
                options.Limits.Http2.MaxRequestHeaderFieldSize = 8192;
                options.Limits.Http2.InitialConnectionWindowSize = 131072;
                options.Limits.Http2.InitialStreamWindowSize = 98304;

                var maxBodyMb = settings.Limits.MaxRequestBodyMB;
                options.Limits.MaxRequestBodySize = maxBodyMb > 0 ? maxBodyMb * 1024L * 1024L : null;

                var maxConn = settings.Limits.MaxConcurrentConnections;
                options.Limits.MaxConcurrentConnections = maxConn > 0 ? maxConn : null;
                options.Limits.MaxConcurrentUpgradedConnections = maxConn > 0 ? maxConn : null;

                options.Limits.MinRequestBodyDataRate =
                    new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
                options.Limits.MinResponseDataRate =
                    new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
            });

        return builder;
    }

    /// <summary>
    /// File logging with rotation plus a timestamped console sink, both honouring the configured level
    /// (<c>App:FileLogging:LogLevel</c>, overridable with <c>CRAFT_LOG_LEVEL</c>).
    /// </summary>
    /// <remarks>
    /// Logging must work before <c>Build()</c>, so this binds <c>App:FileLogging</c> early for the
    /// provider. After Build, call <see cref="SyncFileLoggingFromOptions"/> so rotation/format knobs
    /// match the Options-bound <c>CraftSettings.FileLogging</c> (one logical source post-Build).
    /// Directory/prefix stay as opened at construction.
    /// </remarks>
    /// <returns>
    /// The resolved level and the file provider (needed for the post-Build sync). Startup logs the
    /// level, and it also gates PowerShell stream capture — at Debug, Write-Debug is captured; at
    /// Trace, Write-Verbose as well.
    /// </returns>
    public static (LogLevel Level, FileLoggerProvider FileProvider) AddCraftLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Temporary early bind so logging works during Build; SyncFileLoggingFromOptions aligns
        // mutable knobs with CraftSettings.FileLogging after Options bind.
        var fileLoggingSettings = new FileLoggingSettings();
        builder.Configuration.GetSection("App:FileLogging").Bind(fileLoggingSettings);
        var level = fileLoggingSettings.ParsedLogLevel;

        var fileLoggerProvider = new FileLoggerProvider(fileLoggingSettings, level);
        builder.Logging.AddProvider(fileLoggerProvider);
        LogBridge.Initialize(new LogQueryService(fileLoggerProvider));

        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            options.SingleLine = true;
        });

        if (level > LogLevel.Debug)
        {
            builder.Logging.AddFilter<ConsoleLoggerProvider>(l => l >= LogLevel.Information);

            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Warning);
        }

        return (level, fileLoggerProvider);
    }

    /// <summary>
    /// After <c>Build()</c>, refresh the file logger from Options-bound
    /// <see cref="CraftSettings.FileLogging"/> so AddCraftLogging's early bind does not diverge.
    /// </summary>
    public static void SyncFileLoggingFromOptions(this WebApplication app, FileLoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(provider);

        var settings = app.Services.GetRequiredService<CraftSettings>().FileLogging;
        provider.SyncFrom(settings);
    }

    private static readonly string[] second = new[] { "application/json", "text/json", "application/javascript", "text/javascript" };

    /// <summary>Response compression, matching Azure Static Web Apps behaviour.</summary>
    public static IServiceCollection AddCraftResponseCompression(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                second);
        });

        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

        return services;
    }

    /// <summary>
    /// Registers the Craft service graph gated by deployment roles. Frontend-only nodes skip the
    /// PowerShell / orchestration / auth-store graph.
    /// </summary>
    public static IServiceCollection AddCraftServices(this IServiceCollection services, CraftRoles roles)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(roles);

        services.AddSingleton<StartupProgressService>();
        services.AddSingleton(sp =>
        {
            var health = sp.GetRequiredService<IOptions<CraftSettings>>().Value.ContainerHealth;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ContainerHealthMonitor>();
            return new ContainerHealthMonitor(logger, health);
        });

        // Realtime is mapped for Http or Frontend; cheap when disabled.
        if (roles.Http || roles.Frontend)
            services.AddSingleton<RealtimeService>();

        services.AddSingleton(sp => new CacheService(
            sp.GetRequiredService<ILogger<CacheService>>(),
            sp.GetRequiredService<CraftSettings>(),
            roles.ResponseCacheEnabled));

        if (!roles.RunsPowerShell)
            return services;

        // Shared host tables (orchestrator, health). Auth override must not bleed into these.
        services.AddSingleton<AzureTableStore>();
        services.AddSingleton<ICraftTableStore>(sp => sp.GetRequiredService<AzureTableStore>());
        services.AddSingleton<IUserTableStore>(sp =>
        {
            var settings = sp.GetRequiredService<CraftSettings>();
            if (string.IsNullOrWhiteSpace(settings.Auth.UserStorageConnection))
                return sp.GetRequiredService<AzureTableStore>();
            return new AzureTableStore(settings, settings.Auth.UserStorageConnection, "allowedUsers table");
        });
        services.AddSingleton<StorageHealthMonitor>();
        services.AddSingleton<WorkerMetricsService>();

        services.AddSingleton<ScriptRepository>();
        services.AddSingleton<PowerShellWorkerPool>();
        services.AddSingleton<PowerShellRunnerService>();

        services.AddSingleton<BackgroundTaskLimiter>();
        services.AddSingleton<JobWorkResolver>(sp =>
            sp.GetRequiredService<OrchestratorService>().ResolveTaskWorkAsync);
        services.AddSingleton<IJobDescriptorStateWriter>(sp =>
            sp.GetRequiredService<OrchestratorService>());
        services.AddSingleton<JobManager>();
        services.AddSingleton<OrchestratorTableStore>();
        services.AddSingleton<OrchestratorStatusWriter>();
        services.AddSingleton<OrchestratorService>();
        services.AddSingleton<QueueStatusService>();
        services.AddSingleton<QueueDispatchService>();
        // Empty here; AddNativeEndpoints replaces this when the app ships native scheduled tasks.
        services.AddSingleton(NativeScheduledTasks.Empty);
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<StatsHistoryService>();

        if (roles.Http)
        {
            services.AddSingleton<AuthService>();
        }

        // Setup/AppLifecycle is used from warmup PS on any PowerShell node.
        services.AddSingleton<SetupSessionState>();
        services.AddSingleton<SetupProvisioningService>();
        services.AddSingleton<SetupUserBootstrap>();
        services.AddSingleton<SetupService>();

        // Shutdown flush for PS ingress + tracked drain/planner work (Http and/or Background).
        services.AddHostedService<PendingWorkFlushHostedService>();

        if (roles.Background)
        {
            services.AddHostedService(sp => sp.GetRequiredService<JobManager>());
            services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
            services.AddHostedService(sp => sp.GetRequiredService<StatsHistoryService>());
        }

        return services;
    }

    /// <summary>
    /// Seconds to advertise in <c>Retry-After</c> on a throttled response. Prefers the limiter's own
    /// estimate of when a permit next frees up, falling back to the whole window — a safe upper bound
    /// for a fixed window, and the only figure available when the lease carries no metadata.
    /// </summary>
    public static int ResolveRetryAfterSeconds(RateLimitLease lease, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata)
            ? metadata
            : window;

        return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
    }

    /// <summary>
    /// Per-client fixed-window rate limiter from Options-bound settings. Registers always; when
    /// disabled the limiter is a no-op partition that never rejects.
    /// </summary>
    public static IServiceCollection AddCraftRateLimiter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter();
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<CraftSettings>>((options, craft) =>
            {
                var settings = craft.Value;
                if (!settings.RateLimit.IsEnabled) return;

                var window = TimeSpan.FromSeconds(Math.Max(1, settings.RateLimit.WindowSeconds));

                options.RejectionStatusCode = 429;
                options.OnRejected = (context, _) =>
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ResolveRetryAfterSeconds(context.Lease, window)
                            .ToString(CultureInfo.InvariantCulture);
                    return ValueTask.CompletedTask;
                };

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        RateLimitPartitionKey.Resolve(context),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = Math.Max(1, settings.RateLimit.PermitPerWindow),
                            Window = window,
                            QueueLimit = Math.Max(0, settings.RateLimit.QueueLimit),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        }));
            });

        return services;
    }
}
