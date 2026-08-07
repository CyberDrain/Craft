namespace Craft.Configuration;

/// <summary>
/// Azure Storage connection policy for the shared host store (orchestrator tables, health probes).
/// Resolution order for <see cref="ResolveConnection"/> when no per-call override is passed:
/// <c>AzureWebJobsStorage</c> → <see cref="ConnectionString"/> → Development emulator when allowed.
///
/// The allowedUsers table may use a separate account via <c>Auth:UserStorageConnection</c>
/// (<see cref="AuthSettings.UserStorageConnection"/>); that override is applied only by the user-table
/// store, not here.
///
/// If nothing is configured the host does NOT silently fall back to the local storage emulator
/// (<c>UseDevelopmentStorage=true</c>) in production — that would point host state at a non-existent
/// emulator. The fallback is only used when explicitly opted in; otherwise
/// <see cref="ResolveConnection"/> throws and the host fails to start.
/// </summary>
public class StorageSettings
{
    /// <summary>
    /// Shared Azure Storage connection string — same role as the <c>AzureWebJobsStorage</c> env var.
    /// Containers should keep using the env var. For local <c>dotnet run</c>, set it via user secrets:
    /// <c>dotnet user-secrets set "AzureWebJobsStorage" "…"</c> (copied onto this property at startup)
    /// or <c>dotnet user-secrets set "App:Storage:ConnectionString" "…"</c>.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Allow the local storage emulator fallback (<c>UseDevelopmentStorage=true</c>) when no real
    /// connection string is configured. Default false (fail closed). Also enabled by the
    /// <c>CRAFT_ALLOW_DEV_STORAGE=true</c> environment variable or when
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c>.
    /// </summary>
    public bool AllowDevelopmentStorage { get; set; }

    /// <summary>
    /// Maximum concurrent TCP connections CRAFT opens to a single storage endpoint
    /// (e.g. <c>&lt;account&gt;.table.core.windows.net</c>). Azure Table Storage is HTTP/1.1 with no
    /// multiplexing, so this caps the number of simultaneous in-flight table requests — it bounds
    /// outbound sockets so a job fan-out can't exhaust the host's connection/SNAT budget (Azure App
    /// Service starts throttling around ~128 concurrent outbound connections). This is the same
    /// connection-reuse/limit lever the downstream AzBobbyTables module uses.
    /// Default <b>30</b> (matches the proven Function Apps setting; a storage account is fronted by several
    /// servers, so a modest per-endpoint cap still leaves plenty of aggregate throughput while keeping the
    /// host well under its outbound ceiling). Set to 0 or a negative value for unlimited (the Azure SDK
    /// default). Applies only to the Azure Tables provider. Env override:
    /// <c>CRAFT_STORAGE_MAX_CONNECTIONS_PER_SERVER</c>.
    /// </summary>
    public int MaxConnectionsPerServer { get; set; } = 30;

    /// <summary>
    /// How long (minutes) a pooled storage connection may be reused before it is recycled. Periodic
    /// recycling lets DNS changes propagate and prevents long-idle SNAT ports from wedging. Default
    /// <b>30</b>. Set to 0 or a negative value to disable recycling (connections live as long as usable,
    /// the raw runtime default). Applies only to the Azure Tables provider. Env override:
    /// <c>CRAFT_STORAGE_POOLED_CONNECTION_LIFETIME_MINUTES</c>.
    /// </summary>
    public int PooledConnectionLifetimeMinutes { get; set; } = 30;

    private bool DevStorageAllowed =>
        AllowDevelopmentStorage
        || string.Equals(Environment.GetEnvironmentVariable("CRAFT_ALLOW_DEV_STORAGE"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a Table Storage connection string, failing closed in production when nothing is
    /// configured. <paramref name="explicitConnection"/> is the per-feature override (may be null);
    /// <paramref name="purpose"/> is used only in the exception message for diagnosability.
    /// </summary>
    public string ResolveConnection(string? explicitConnection, string purpose)
    {
        if (!string.IsNullOrWhiteSpace(explicitConnection)) return explicitConnection;
        var env = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString;
        if (DevStorageAllowed) return "UseDevelopmentStorage=true";
        throw new InvalidOperationException(
            $"No Azure Storage connection is configured for {purpose}. Set the AzureWebJobsStorage " +
            "environment variable or App:Storage:ConnectionString, or for local Development use " +
            "`dotnet user-secrets set \"AzureWebJobsStorage\" \"…\"`. For the allowedUsers table only, " +
            "App:Auth:UserStorageConnection may isolate that table. To use the local storage emulator, " +
            "set ASPNETCORE_ENVIRONMENT=Development, App:Storage:AllowDevelopmentStorage=true, or " +
            "CRAFT_ALLOW_DEV_STORAGE=true.");
    }
}
