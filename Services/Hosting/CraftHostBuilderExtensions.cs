using System.Globalization;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Craft.Auth;
using Craft.Caching;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Realtime;
using Craft.Services;
using Craft.Setup;
using Craft.Storage;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Craft.Hosting;

/// <summary>
/// Host wiring, split out of <c>Program.cs</c> so startup reads as a short sequence of named steps
/// rather than several hundred lines of inline configuration.
/// </summary>
public static class CraftHostBuilderExtensions
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
    /// <c>max(ProcessorCount * 4, 32)</c>.
    /// <para>
    /// The pool term is the important part. PowerShell blocks a thread for the duration of every
    /// outbound call it makes, so a pool of N workers can park N threads simultaneously; if the
    /// thread-pool minimum is below that, the CLR has to inject the difference at about one thread
    /// per second before the pool can reach its own concurrency. Sizing the minimum off cores alone
    /// — as this did — meant a 1-core container floored at 32 and any pool above that paid the ramp
    /// on every restart.
    /// </para>
    /// The +16 is headroom for the runtime's own work (Kestrel, timers, the storage SDK) so those do
    /// not have to contend with parked PowerShell threads for the same minimum.
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
    /// Kestrel limits: request timeouts, HTTP/2 tuning, and the DoS-relevant caps (body size,
    /// connection count, slow-loris minimum data rates). The caps apply regardless of the timeout.
    /// </summary>
    public static WebApplicationBuilder ConfigureCraftKestrel(
        this WebApplicationBuilder builder, CraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);

        var timeout = ResolveKestrelTimeoutSeconds(settings);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(timeout);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(Math.Min(60, timeout));

            // HTTP/2 — better multiplexing for the browser UI.
            options.Limits.Http2.MaxStreamsPerConnection = 100;
            options.Limits.Http2.HeaderTableSize = 4096;
            options.Limits.Http2.MaxFrameSize = 16384;
            options.Limits.Http2.MaxRequestHeaderFieldSize = 8192;
            options.Limits.Http2.InitialConnectionWindowSize = 131072;
            options.Limits.Http2.InitialStreamWindowSize = 98304;

            // Request body cap. Default 100 MB; 0 means unlimited.
            var maxBodyMb = settings.Limits.MaxRequestBodyMB;
            options.Limits.MaxRequestBodySize = maxBodyMb > 0 ? maxBodyMb * 1024L * 1024L : null;

            // Concurrent connection cap. Default 200; <= 0 hands the decision to the OS.
            var maxConn = settings.Limits.MaxConcurrentConnections;
            options.Limits.MaxConcurrentConnections = maxConn > 0 ? maxConn : null;
            options.Limits.MaxConcurrentUpgradedConnections = maxConn > 0 ? maxConn : null;

            // Slow-loris protection.
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
    /// <returns>
    /// The resolved level. Startup logs it, and it also gates PowerShell stream capture — at Debug,
    /// Write-Debug is captured; at Trace, Write-Verbose as well.
    /// </returns>
    public static LogLevel AddCraftLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var fileLoggingSettings = new FileLoggingSettings();
        builder.Configuration.GetSection("App:FileLogging").Bind(fileLoggingSettings);
        var level = fileLoggingSettings.ParsedLogLevel;

        var fileLoggerProvider = new FileLoggerProvider(fileLoggingSettings, level);
        builder.Logging.AddProvider(fileLoggerProvider);
        LogBridge.Initialize(fileLoggerProvider);

        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            options.SingleLine = true;
        });

        if (level > LogLevel.Debug)
        {
            builder.Logging.AddFilter<ConsoleLoggerProvider>(l => l >= LogLevel.Information);

            // Framework logging is noise at Information and above.
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Warning);
        }

        return level;
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

        // Fastest, not Optimal: these run on the request path on a small container, where the extra
        // CPU costs more than the bytes saved. Precompressed .br/.gz siblings cover the static assets.
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

        return services;
    }

    /// <summary>
    /// Registers the Craft service graph. Background hosted services are registered only on nodes
    /// carrying the Background role — every node builds the same object graph, but only a Background
    /// node actually runs the scheduler, job manager and stats sampler.
    /// </summary>
    public static IServiceCollection AddCraftServices(this IServiceCollection services, CraftRoles roles)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(roles);

        services.AddSingleton<ICraftTableStore, AzureTableStore>();
        services.AddSingleton<StorageHealthMonitor>();
        services.AddSingleton<RealtimeService>();

        services.AddSingleton<ScriptRepository>();
        services.AddSingleton<PowerShellWorkerPool>();
        services.AddSingleton<PowerShellRunnerService>();

        services.AddSingleton(sp => new CacheService(
            sp.GetRequiredService<ILogger<CacheService>>(),
            sp.GetRequiredService<CraftSettings>(),
            roles.ResponseCacheEnabled));

        services.AddSingleton<BackgroundTaskLimiter>();
        services.AddSingleton<JobManager>();
        services.AddSingleton<OrchestratorTableStore>();
        services.AddSingleton<OrchestratorStatusWriter>();
        services.AddSingleton<OrchestratorService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<StatsHistoryService>();

        if (roles.Background)
        {
            services.AddHostedService(sp => sp.GetRequiredService<JobManager>());
            services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
            services.AddHostedService(sp => sp.GetRequiredService<StatsHistoryService>());
        }

        services.AddSingleton(sp =>
        {
            var health = sp.GetRequiredService<IOptions<CraftSettings>>().Value.ContainerHealth;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ContainerHealthMonitor>();
            return new ContainerHealthMonitor(logger, health);
        });

        return services;
    }

    /// <summary>
    /// Seconds to advertise in <c>Retry-After</c> on a throttled response. Prefers the limiter's own
    /// estimate of when a permit next frees up, falling back to the whole window — a safe upper bound
    /// for a fixed window, and the only figure available when the lease carries no metadata.
    /// </summary>
    /// <remarks>
    /// Rounded up, and floored at one second, on purpose. Truncating a 0.4s wait to
    /// <c>Retry-After: 0</c> tells a well-behaved client to retry immediately, which turns being
    /// throttled into a hot loop — the opposite of what the header is for.
    /// </remarks>
    public static int ResolveRetryAfterSeconds(RateLimitLease lease, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata)
            ? metadata
            : window;

        return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
    }

    /// <summary>
    /// Per-client fixed-window rate limiter so a single caller cannot exhaust the small HTTP worker
    /// pool. Enabled by default; turn off with <c>App:RateLimit:Enabled=false</c>. Throttled requests
    /// get a 429 carrying <c>Retry-After</c>.
    /// </summary>
    public static IServiceCollection AddCraftRateLimiter(
        this IServiceCollection services, CraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.RateLimit.IsEnabled) return services;

        var window = TimeSpan.FromSeconds(Math.Max(1, settings.RateLimit.WindowSeconds));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;

            // Without this the 429 carries no timing hint at all and every caller has to invent its
            // own backoff. Retry-After is the one thing HTTP clients and SDKs already know how to
            // honour unprompted, so emitting it is what makes the limit self-documenting.
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
