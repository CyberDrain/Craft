using System.Collections.Concurrent;
using System.Text.Json;
using Craft.Configuration;
using Craft.Storage;

namespace Craft.Auth;

/// <summary>
/// Transforms Azure App Service EasyAuth headers into the SWA client-principal format the hosted
/// app consumes, and authorizes callers against a configurable Azure Table (allowedUsers).
/// Authentication itself is owned by the upstream EasyAuth platform.
/// </summary>
public class AuthService : IDisposable
{
    private readonly ILogger<AuthService> _logger;
    private readonly CraftSettings _settings;
    private readonly IUserTableStore _store;

    // allowedUsers cache
    private readonly ConcurrentDictionary<string, AllowedUser> _allowedUsersCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _allowedUsersCacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _allowedUsersCacheTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _allowedUsersLock = new(1, 1);

    public AuthService(ILogger<AuthService> logger, CraftSettings settings, IUserTableStore store)
    {
        _logger = logger;
        _settings = settings;
        _store = store;
    }

    // --- Configuration ---
    // IsConfigured reflects whether Azure App Service EasyAuth is set up: the platform sets
    // WEBSITE_AUTH_CLIENT_ID when its Microsoft identity provider is configured.

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_AUTH_CLIENT_ID"));

    // --- User Table ---

    /// <summary>
    /// The Azure Table name for user authorization.
    /// Reads from Auth:UserTableName in config (default "allowedUsers").
    /// Override with Auth__UserTableName env var or set directly in appsettings.
    /// Sanitized for Azure Table naming rules (alphanumeric, 3-63 chars).
    /// </summary>
    private string? _resolvedTableName;
    public string UserTableFullName
    {
        get
        {
            if (_resolvedTableName != null) return _resolvedTableName;
            var raw = _settings.Auth.UserTableName;
            // Sanitize: Azure Table names are alphanumeric only, 3-63 chars
            var sanitized = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (sanitized.Length > 63) sanitized = sanitized[..63];
            if (sanitized.Length < 3) sanitized = "allowedUsers";
            _resolvedTableName = sanitized;
            return _resolvedTableName;
        }
    }

    /// <summary>
    /// Clears the allowedUsers cache. Call after auth credentials/config are updated at runtime
    /// (e.g. after the setup wizard) so the next authorization check re-reads the table.
    /// </summary>
    public void ReloadConfiguration()
    {
        _allowedUsersCacheExpiry = DateTime.MinValue;
        _logger.LogInformation("[Auth] Configuration reloaded — allowedUsers cache cleared. IsConfigured={IsConfigured}", IsConfigured);
    }

    /// <summary>
    /// Invalidates the allowedUsers cache so it is refreshed on the next auth check.
    /// </summary>
    public void InvalidateUserCache()
    {
        _allowedUsersCacheExpiry = DateTime.MinValue;
        _allowedUsersCache.Clear();
    }

    // --- allowedUsers Table ---

    /// <summary>
    /// Gets the CIPP roles for a user from the allowedUsers table.
    /// Returns null if user is not authorized (not in table and AllowAllTenantUsers is false).
    /// Returns ["anonymous", "authenticated"] as default roles if AllowAllTenantUsers is true.
    /// </summary>
    public async Task<string[]?> GetUserRoles(string upn, CancellationToken ct = default)
    {
        var user = await GetAllowedUser(upn, ct);
        if (user == null)
        {
            if (!_settings.Auth.AllowAllTenantUsers)
            {
                _logger.LogWarning("[Auth] User {Upn} not in allowedUsers table — denied (AllowAllTenantUsers=false)", upn);
                return null;
            }
            return new[] { "anonymous", "authenticated" };
        }
        var roles = new List<string>(user.Roles);
        if (!roles.Contains("anonymous")) roles.Add("anonymous");
        if (!roles.Contains("authenticated")) roles.Add("authenticated");
        return roles.ToArray();
    }

    private async Task<AllowedUser?> GetAllowedUser(string upn, CancellationToken ct)
    {
        // Check cache first
        if (DateTime.UtcNow < _allowedUsersCacheExpiry && _allowedUsersCache.TryGetValue(upn, out var cached))
        {
            return cached;
        }

        // Refresh cache if expired
        await _allowedUsersLock.WaitAsync(ct);
        try
        {
            // Double-check after lock
            if (DateTime.UtcNow < _allowedUsersCacheExpiry && _allowedUsersCache.TryGetValue(upn, out var cachedAfterLock))
            {
                return cachedAfterLock;
            }

            if (DateTime.UtcNow >= _allowedUsersCacheExpiry)
            {
                await RefreshAllowedUsersCache(ct);
            }

            return _allowedUsersCache.TryGetValue(upn, out var user) ? user : null;
        }
        finally
        {
            _allowedUsersLock.Release();
        }
    }

    private async Task RefreshAllowedUsersCache(CancellationToken ct)
    {
        try
        {
            await _store.EnsureTableAsync(UserTableFullName, ct);

            var newCache = new Dictionary<string, AllowedUser>(StringComparer.OrdinalIgnoreCase);

            await foreach (var row in _store.QueryTableAsync(UserTableFullName, ct))
            {
                // Skip internal rows
                if (row.RowKey.StartsWith('_')) continue;

                // Normalize the UPN to lowercase so case-variant duplicate rows
                // resolve to a single entry.
                var upn = row.RowKey.ToLowerInvariant();
                var rolesJson = row.GetString("Roles") ?? "[]";
                string[] roles;
                try
                {
                    roles = JsonSerializer.Deserialize<string[]>(rolesJson) ?? Array.Empty<string>();
                }
                catch
                {
                    roles = new[] { rolesJson }; // Fallback: single role as plain string
                }

                if (newCache.TryGetValue(upn, out var existing))
                {
                    // Duplicate case-variant row — union the roles (case-sensitive dedupe)
                    // so no role assignment is silently dropped by last-writer-wins.
                    var merged = new List<string>(existing.Roles);
                    foreach (var role in roles)
                    {
                        if (!merged.Contains(role, StringComparer.Ordinal))
                        {
                            merged.Add(role);
                        }
                    }
                    existing.Roles = merged.ToArray();
                }
                else
                {
                    newCache[upn] = new AllowedUser
                    {
                        Upn = upn,
                        Roles = roles
                    };
                }
            }

            _allowedUsersCache.Clear();
            foreach (var kv in newCache)
            {
                _allowedUsersCache[kv.Key] = kv.Value;
            }
            _allowedUsersCacheExpiry = DateTime.UtcNow.Add(_allowedUsersCacheTtl);
            _logger.LogInformation("[Auth] Refreshed allowedUsers cache: {Count} users", newCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Auth] Failed to refresh allowedUsers cache");
            // Keep stale cache on error — better than locking everyone out
            _allowedUsersCacheExpiry = DateTime.UtcNow.AddMinutes(1); // Retry sooner
        }
    }

    // --- Data Models ---

    private sealed class AllowedUser
    {
        public string Upn { get; set; } = "";
        public string[] Roles { get; set; } = Array.Empty<string>();
    }

    public void Dispose()
    {
        // Nothing here owns unmanaged resources directly, but suppressing finalization keeps a
        // derived type that adds a finalizer from having to re-implement IDisposable to do it.
        GC.SuppressFinalize(this);

        // This was a leftover `throw new NotImplementedException()` from the IDisposable stub. DI
        // disposes singletons in reverse creation order and does not catch, so it aborted the whole
        // chain: every service created before AuthService (the worker pool, runner, cache, realtime,
        // script repo, table store) was skipped, and the host exited with an unhandled exception on
        // EVERY shutdown.
        _allowedUsersLock.Dispose();
    }
}
