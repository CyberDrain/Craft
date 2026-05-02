using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CRAFT.Services;

/// <summary>
/// Static bridge so PowerShell can trigger auth reload without DI.
/// Call [CRAFT.Services.AuthBridge]::ReloadAuth() from PS after credentials change.
/// </summary>
public static class AuthBridge
{
    private static AuthService? s_service;
    public static void Initialize(AuthService service) => s_service = service;

    /// <summary>
    /// Reloads OIDC configuration after auth credentials are updated.
    /// Safe to call from PowerShell: [CRAFT.Services.AuthBridge]::ReloadAuth()
    /// </summary>
    public static void ReloadAuth() => s_service?.ReloadConfiguration();
}

/// <summary>
/// Handles Azure AD OIDC authentication, JWT validation, session management,
/// and user authorization via a configurable Azure Table.
/// </summary>
public class AuthService
{
  private readonly ILogger<AuthService> _logger;
  private readonly IConfiguration _config;
  private readonly CraftSettings _settings;

  // OIDC discovery + signing key cache (refreshes automatically)
  private ConfigurationManager<OpenIdConnectConfiguration>? _oidcConfigManager;
  private string? _resolvedTenantId;

  // In-memory session store: sessionId → SessionData
  private readonly ConcurrentDictionary<string, SessionData> _sessions = new();

  // allowedUsers cache
  private readonly ConcurrentDictionary<string, AllowedUser> _allowedUsersCache = new(StringComparer.OrdinalIgnoreCase);
  private DateTime _allowedUsersCacheExpiry = DateTime.MinValue;
  private readonly TimeSpan _allowedUsersCacheTtl = TimeSpan.FromMinutes(5);
  private readonly SemaphoreSlim _allowedUsersLock = new(1, 1);

  // Setup invite (first-run)
  // In-memory: maps token plaintext → invite entry for fast validation
  // (see ProcessPendingInvitesAsync for the table-based invite flow)

  // Shared HttpClient for token exchange (singleton-safe, handles DNS rotation)
  private static readonly HttpClient s_httpClient;

  static AuthService()
  {
    var handler = new SocketsHttpHandler
    {
      PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    };
    s_httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
    s_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CRAFT-AuthService/1.0");
  }

  // Cookie & crypto settings
  private byte[]? _cookieKey; // AES-256 key for encrypting session cookies

  public AuthService(ILogger<AuthService> logger, IConfiguration config, CraftSettings settings)
  {
    _logger = logger;
    _config = config;
    _settings = settings;
  }

  // --- Configuration ---
  // Uses standard Azure App Service env vars:
  //   WEBSITE_AUTH_CLIENT_ID           — AAD app registration client/app ID
  //   AUTH_SECRET                      — AAD client secret
  //   WEBSITE_AUTH_AAD_ALLOWED_TENANTS — tenant ID

  public string ClientId => Environment.GetEnvironmentVariable("WEBSITE_AUTH_CLIENT_ID")
                            ?? _config.GetValue<string>("Auth:ClientId")
                            ?? throw new InvalidOperationException("WEBSITE_AUTH_CLIENT_ID not configured");

  public string ClientSecret => Environment.GetEnvironmentVariable("AUTH_SECRET")
                                ?? _config.GetValue<string>("Auth:ClientSecret")
                                ?? throw new InvalidOperationException("AUTH_SECRET not configured");

  public string TenantId => Environment.GetEnvironmentVariable("WEBSITE_AUTH_AAD_ALLOWED_TENANTS")
                            ?? _config.GetValue<string>("Auth:TenantId")
                            ?? "common";

  public bool IsConfigured => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_AUTH_CLIENT_ID"))
                              || !string.IsNullOrEmpty(_config.GetValue<string>("Auth:ClientId"));

  // --- Instance ID & per-instance table ---

  /// <summary>
  /// Stable identifier for this CRAFT deployment. Used to derive a per-instance
  /// user table name so multiple deployments sharing storage are fully isolated.
  /// Must not change across container restarts, updates, or scaling events.
  /// Set via WEBSITE_DEPLOYMENT_ID (Azure) or CRAFT_INSTANCE_ID (self-hosted).
  /// </summary>
  public string InstanceId =>
      Environment.GetEnvironmentVariable("WEBSITE_DEPLOYMENT_ID")
      ?? Environment.GetEnvironmentVariable("CRAFT_INSTANCE_ID")
      ?? throw new InvalidOperationException(
          "No stable instance ID configured. Set WEBSITE_DEPLOYMENT_ID or CRAFT_INSTANCE_ID environment variable. "
          + "This is required to scope user access and setup invites per deployment.");

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

  // --- Setup Invites (first-run user provisioning without OIDC) ---
  //
  // Flow:
  //   1. Deployer inserts a row into the instance user table (e.g. allowedUsersCippProd01):
  //      PartitionKey="", RowKey={email}, Roles=["superadmin",...], InviteStatus="PendingInvite"
  //   2. CRAFT polls the table on startup and periodically (when auth unconfigured).
  //      For each PendingInvite row → generates token → writes InviteToken (hash),
  //      InviteUrl, InviteStatus="InviteReady", InviteExpiresAt.
  //   3. Deployer/portal reads the updated row → shows InviteUrl to end user.
  //   4. User clicks URL → CRAFT validates hash → creates session → user does SAM setup.
  //   5. After OIDC is configured, invite rows are cleaned up.

  // In-memory: maps token plaintext → (upn, roles) for fast validation
  private readonly ConcurrentDictionary<string, InviteEntry> _activeInvites = new(StringComparer.Ordinal);

  private class InviteEntry
  {
    public string Upn { get; set; } = "";
    public string[] Roles { get; set; } = Array.Empty<string>();
    public DateTime ExpiresAt { get; set; }
  }

  /// <summary>
  /// Scans the allowedUsers table for PendingInvite rows, generates tokens,
  /// and updates the rows with the invite URL. Also refreshes InviteReady
  /// tokens into the in-memory cache for validation.
  /// Call on startup and periodically when auth is not configured.
  /// </summary>
  public async Task ProcessPendingInvitesAsync(string? baseUrl = null, CancellationToken ct = default)
  {
    try
    {
      var client = new TableClient(StorageConnectionString, UserTableFullName);
      await client.CreateIfNotExistsAsync(cancellationToken: ct);

      var processed = 0;
      var loaded = 0;

      await foreach (var entity in client.QueryAsync<TableEntity>(cancellationToken: ct))
      {
        var status = entity.GetString("InviteStatus") ?? "";

        if (status == "PendingInvite")
        {
          // Generate token and update row
          var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
          var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
          var expiresAt = DateTimeOffset.UtcNow.AddHours(72);
          var setupPath = _settings.Auth.SetupPath;
          var inviteUrl = string.IsNullOrEmpty(baseUrl)
              ? $"{setupPath}?setup_token={token}"
              : $"{baseUrl.TrimEnd('/')}{setupPath}?setup_token={token}";

          entity["InviteStatus"] = "InviteReady";
          entity["InviteToken"] = tokenHash;
          entity["InviteUrl"] = inviteUrl;
          entity["InviteExpiresAt"] = expiresAt;
          entity["InviteCreatedAt"] = DateTimeOffset.UtcNow;

          await client.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, ct);

          // Cache for validation
          _activeInvites[token] = new InviteEntry
          {
            Upn = entity.RowKey,
            Roles = ParseRoles(entity.GetString("Roles")),
            ExpiresAt = expiresAt.UtcDateTime
          };

          processed++;
          _logger.LogInformation("[Auth] Invite generated for {Upn} (table={Table})", entity.RowKey, UserTableFullName);
        }
        else if (status == "InviteReady")
        {
          // Load existing invite into memory for validation
          var expiresAt = entity.GetDateTimeOffset("InviteExpiresAt");
          if (expiresAt.HasValue && expiresAt.Value > DateTimeOffset.UtcNow)
          {
            var tokenHash = entity.GetString("InviteToken") ?? "";
            // We can't recover plaintext from hash — but if we generated it
            // in this process lifetime, it's already in _activeInvites.
            // For cross-restart: re-generate if needed.
            if (!_activeInvites.Values.Any(i => i.Upn == entity.RowKey))
            {
              // Token was generated by a previous instance — regenerate
              var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
              var newHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
              var setupPath = _settings.Auth.SetupPath;
              var inviteUrl = string.IsNullOrEmpty(baseUrl)
                  ? $"{setupPath}?setup_token={token}"
                  : $"{baseUrl.TrimEnd('/')}{setupPath}?setup_token={token}";

              entity["InviteToken"] = newHash;
              entity["InviteUrl"] = inviteUrl;
              await client.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, ct);

              _activeInvites[token] = new InviteEntry
              {
                Upn = entity.RowKey,
                Roles = ParseRoles(entity.GetString("Roles")),
                ExpiresAt = expiresAt.Value.UtcDateTime
              };
              loaded++;
              _logger.LogInformation("[Auth] Invite re-generated for {Upn} after restart", entity.RowKey);
            }
            else
            {
              loaded++;
            }
          }
          else
          {
            // Expired — mark as expired
            entity["InviteStatus"] = "InviteExpired";
            await client.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, ct);
            _logger.LogInformation("[Auth] Invite expired for {Upn}", entity.RowKey);
          }
        }
      }

      if (processed > 0 || loaded > 0)
      {
        _logger.LogInformation("[Auth] Invites: {Processed} new, {Loaded} active (table={Table})", processed, loaded, UserTableFullName);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "[Auth] Failed to process pending invites");
    }
  }

  private static string[] ParseRoles(string? rolesJson)
  {
    if (string.IsNullOrEmpty(rolesJson)) return ["superadmin", "authenticated", "anonymous"];
    try { return JsonSerializer.Deserialize<string[]>(rolesJson) ?? ["superadmin", "authenticated", "anonymous"]; }
    catch { return [rolesJson]; }
  }

  /// <summary>
  /// Validates a setup token against active invites and creates a session.
  /// Returns the session ID on success, null if invalid/expired.
  /// </summary>
  public string? ValidateSetupToken(string token)
  {
    if (IsConfigured) return null;

    // Constant-time lookup: check each active invite
    InviteEntry? matched = null;
    string? matchedToken = null;
    foreach (var kvp in _activeInvites)
    {
      if (CryptographicOperations.FixedTimeEquals(
          Encoding.UTF8.GetBytes(token),
          Encoding.UTF8.GetBytes(kvp.Key)))
      {
        matched = kvp.Value;
        matchedToken = kvp.Key;
      }
    }

    if (matched == null || DateTime.UtcNow > matched.ExpiresAt)
      return null;

    // Create a session for this invited user
    var sessionId = Guid.NewGuid().ToString("N");
    _sessions[sessionId] = new SessionData
    {
      Upn = matched.Upn,
      ObjectId = "00000000-0000-0000-0000-000000000000",
      Name = matched.Upn,
      TenantId = "",
      Roles = matched.Roles,
      AccessToken = "",
      IdToken = "",
      ExpiresOn = DateTime.UtcNow.AddHours(2),
      CreatedAt = DateTime.UtcNow
    };

    // Remove used invite from memory (single-use)
    if (matchedToken != null) _activeInvites.TryRemove(matchedToken, out _);

    _logger.LogInformation("[Auth] Invite token validated for {Upn} — session created", matched.Upn);
    return sessionId;
  }

  /// <summary>
  /// Cleans up all invite rows for this instance (called after OIDC is configured).
  /// </summary>
  public async Task CleanupInvitesAsync(CancellationToken ct = default)
  {
    _activeInvites.Clear();
    try
    {
      var client = new TableClient(StorageConnectionString, UserTableFullName);
      await foreach (var entity in client.QueryAsync<TableEntity>(cancellationToken: ct))
      {
        var status = entity.GetString("InviteStatus") ?? "";
        if (status is "PendingInvite" or "InviteReady")
        {
          entity["InviteStatus"] = "Completed";
          entity.Remove("InviteToken");
          entity.Remove("InviteUrl");
          await client.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, ct);
        }
      }
      _logger.LogInformation("[Auth] Invite rows cleaned up (table={Table})", UserTableFullName);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "[Auth] Failed to clean up invite rows");
    }
  }

  private string StorageConnectionString =>
      (!string.IsNullOrEmpty(_settings.Auth.UserStorageConnection)
          ? _settings.Auth.UserStorageConnection
          : Environment.GetEnvironmentVariable("AzureWebJobsStorage"))
      ?? "UseDevelopmentStorage=true";

  private byte[] CookieKey
  {
    get
    {
      if (_cookieKey != null) return _cookieKey;
      // Derive a stable key — use client secret when configured, otherwise instance ID
      var keyMaterial = IsConfigured ? ClientSecret : InstanceId;
      _cookieKey = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial + "_craft_session_key"));
      return _cookieKey;
    }
  }

  // --- OIDC Discovery ---

  /// <summary>
  /// Resets cached OIDC configuration, cookie key, and allowed users cache.
  /// Call after auth credentials (env vars) are updated at runtime (e.g. after setup wizard).
  /// </summary>
  public void ReloadConfiguration()
  {
    _oidcConfigManager = null;
    _resolvedTenantId = null;
    _cookieKey = null;
    _allowedUsersCacheExpiry = DateTime.MinValue;
    _sessions.Clear();
    if (IsConfigured)
    {
      _activeInvites.Clear();
      // Fire-and-forget cleanup of invite rows (best effort)
      _ = Task.Run(async () =>
      {
        try { await CleanupInvitesAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Auth] Failed to clean up invites"); }
      });
    }
    _logger.LogInformation("[Auth] Configuration reloaded — OIDC, sessions, and caches cleared. IsConfigured={IsConfigured}", IsConfigured);
  }

  private ConfigurationManager<OpenIdConnectConfiguration> GetOidcConfigManager()
  {
    if (_oidcConfigManager != null) return _oidcConfigManager;
    var metadataUrl = $"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration";
    _oidcConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
        metadataUrl,
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = true });
    _logger.LogInformation("[Auth] OIDC discovery: {Url}", metadataUrl);
    return _oidcConfigManager;
  }

  // --- Login Flow ---

  /// <summary>
  /// Generates the Azure AD authorization URL for the OIDC login flow.
  /// </summary>
  public string GetLoginUrl(string redirectUri, string? postLoginRedirect = null)
  {
    var nonce = Guid.NewGuid().ToString("N");
    var state = postLoginRedirect ?? "/";

    var authUrl = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize"
                  + $"?client_id={Uri.EscapeDataString(ClientId)}"
                  + $"&response_type=code"
                  + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                  + $"&response_mode=query"
                  + $"&scope={Uri.EscapeDataString("openid profile email")}"
                  + $"&state={Uri.EscapeDataString(state)}"
                  + $"&nonce={nonce}"
                  + $"&prompt=select_account";

    return authUrl;
  }

  /// <summary>
  /// Exchanges an authorization code for tokens, validates the id_token,
  /// checks allowedUsers, and creates a session.
  /// Returns (sessionId, postLoginRedirect) on success, or throws.
  /// </summary>
  public async Task<(string SessionId, string RedirectUrl)> HandleCallback(
      string code, string state, string redirectUri, CancellationToken ct = default)
  {
    // 1. Exchange code for tokens
    var tokenResponse = await ExchangeCodeForTokens(code, redirectUri, ct);

    // 2. Validate the id_token signature and claims
    var principal = await ValidateIdToken(tokenResponse.IdToken, ct);
    var upn = principal.FindFirst("preferred_username")?.Value
              ?? principal.FindFirst("email")?.Value
              ?? principal.FindFirst("upn")?.Value
              ?? throw new SecurityTokenException("Token missing user identifier claim");

    var oid = principal.FindFirst("oid")?.Value
              ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
              ?? "";

    var name = principal.FindFirst("name")?.Value ?? upn;
    var tid = principal.FindFirst("tid")?.Value
              ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
              ?? "";

    // Cache the tenant ID from the actual token for future OIDC validation
    if (!string.IsNullOrEmpty(tid) && TenantId == "common")
    {
      _resolvedTenantId = tid;
    }

    _logger.LogInformation("[Auth] Token validated for {Upn} (oid={Oid})", upn, oid);

    // 3. Check allowedUsers table
    var allowedUser = await GetAllowedUser(upn, ct);
    string[] roles;
    if (allowedUser != null)
    {
      roles = allowedUser.Roles;
      _logger.LogInformation("[Auth] User {Upn} authorized with roles: {Roles}", upn, string.Join(",", roles));
    }
    else if (_settings.Auth.AllowAllTenantUsers)
    {
      roles = ["authenticated", "anonymous"];
      _logger.LogInformation("[Auth] User {Upn} not in table — allowed with default roles (AllowAllTenantUsers=true)", upn);
    }
    else
    {
      _logger.LogWarning("[Auth] User {Upn} not in allowedUsers table — denied", upn);
      throw new UnauthorizedAccessException($"User '{upn}' is not authorized. Contact your administrator.");
    }

    // 4. Create session
    var sessionId = Guid.NewGuid().ToString("N");
    var session = new SessionData
    {
      Upn = upn,
      ObjectId = oid,
      Name = name,
      TenantId = tid,
      Roles = roles,
      AccessToken = tokenResponse.AccessToken,
      IdToken = tokenResponse.IdToken,
      ExpiresOn = tokenResponse.ExpiresOn,
      CreatedAt = DateTime.UtcNow
    };
    _sessions[sessionId] = session;

    var redirect = string.IsNullOrEmpty(state) || state == "/" ? "/" : state;
    return (sessionId, redirect);
  }

  // --- Token Exchange ---

  private async Task<TokenResponse> ExchangeCodeForTokens(string code, string redirectUri, CancellationToken ct)
  {
    var tokenEndpoint = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";

    var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
      ["client_id"] = ClientId,
      ["client_secret"] = ClientSecret,
      ["code"] = code,
      ["redirect_uri"] = redirectUri,
      ["grant_type"] = "authorization_code",
      ["scope"] = "openid profile email"
    });

    var response = await s_httpClient.PostAsync(tokenEndpoint, content, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
    {
      _logger.LogError("[Auth] Token exchange failed: {Status} {Body}", response.StatusCode, body);
      throw new InvalidOperationException($"Token exchange failed: {response.StatusCode}");
    }

    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;
    var accessToken = root.GetProperty("access_token").GetString()!;
    var idToken = root.GetProperty("id_token").GetString()!;
    var expiresIn = root.GetProperty("expires_in").GetInt32();

    return new TokenResponse
    {
      AccessToken = accessToken,
      IdToken = idToken,
      ExpiresOn = DateTime.UtcNow.AddSeconds(expiresIn)
    };
  }

  // --- JWT Validation ---

  /// <summary>
  /// Validates the id_token signature against Azure AD's published signing keys.
  /// Also validates issuer, audience, and expiry.
  /// </summary>
  public async Task<System.Security.Claims.ClaimsPrincipal> ValidateIdToken(string idToken, CancellationToken ct = default)
  {
    var oidcConfig = await GetOidcConfigManager().GetConfigurationAsync(ct);

    var validationParams = new TokenValidationParameters
    {
      ValidateIssuerSigningKey = true,
      IssuerSigningKeys = oidcConfig.SigningKeys,
      ValidateIssuer = true,
      ValidIssuers = new[]
        {
                $"https://login.microsoftonline.com/{TenantId}/v2.0",
                $"https://login.microsoftonline.com/{_resolvedTenantId ?? TenantId}/v2.0",
                $"https://sts.windows.net/{TenantId}/",
                $"https://sts.windows.net/{_resolvedTenantId ?? TenantId}/"
            },
      ValidateAudience = true,
      ValidAudience = ClientId,
      ValidateLifetime = true,
      ClockSkew = TimeSpan.FromMinutes(5)
    };

    var handler = new JwtSecurityTokenHandler();
    var principal = handler.ValidateToken(idToken, validationParams, out _);
    return principal;
  }

  // --- allowedUsers Table ---

  /// <summary>
  /// Gets the CIPP roles for a user from the allowedUsers table.
  /// Returns ["anonymous", "authenticated"] if user not found (no CIPP roles).
  /// </summary>
  public async Task<string[]> GetUserRoles(string upn, CancellationToken ct = default)
  {
    var user = await GetAllowedUser(upn, ct);
    if (user == null)
    {
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
      var client = new TableClient(StorageConnectionString, UserTableFullName);
      await client.CreateIfNotExistsAsync(cancellationToken: ct);

      var newCache = new Dictionary<string, AllowedUser>(StringComparer.OrdinalIgnoreCase);

      await foreach (var entity in client.QueryAsync<TableEntity>(cancellationToken: ct))
      {
        // Skip internal rows (setup invite, etc.)
        if (entity.RowKey.StartsWith("_")) continue;

        var upn = entity.RowKey;
        var rolesJson = entity.GetString("Roles") ?? "[]";
        string[] roles;
        try
        {
          roles = JsonSerializer.Deserialize<string[]>(rolesJson) ?? Array.Empty<string>();
        }
        catch
        {
          roles = new[] { rolesJson }; // Fallback: single role as plain string
        }

        newCache[upn] = new AllowedUser
        {
          Upn = upn,
          Roles = roles
        };
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

  // --- Session Management ---

  /// <summary>
  /// Gets the session for a request by reading the encrypted session cookie.
  /// Returns null if no valid session.
  /// </summary>
  public SessionData? GetSession(HttpContext context)
  {
    if (!context.Request.Cookies.TryGetValue(_settings.Auth.CookieName, out var cookieValue))
      return null;

    var sessionId = DecryptSessionId(cookieValue);
    if (sessionId == null) return null;

    if (!_sessions.TryGetValue(sessionId, out var session)) return null;

    // Check if session has expired (token expiry + 1 hour grace)
    if (DateTime.UtcNow > session.ExpiresOn.AddHours(1))
    {
      _sessions.TryRemove(sessionId, out _);
      return null;
    }

    return session;
  }

  /// <summary>
  /// Sets the encrypted session cookie on the response.
  /// </summary>
  public void SetSessionCookie(HttpContext context, string sessionId)
  {
    var encrypted = EncryptSessionId(sessionId);
    context.Response.Cookies.Append(_settings.Auth.CookieName, encrypted, new CookieOptions
    {
      HttpOnly = true,
      Secure = context.Request.IsHttps,
      SameSite = SameSiteMode.Lax,
      Path = "/",
      MaxAge = TimeSpan.FromHours(8)
    });
  }

  /// <summary>
  /// Removes the session cookie and deletes the session.
  /// </summary>
  public void ClearSession(HttpContext context)
  {
    if (context.Request.Cookies.TryGetValue(_settings.Auth.CookieName, out var cookieValue))
    {
      var sessionId = DecryptSessionId(cookieValue);
      if (sessionId != null)
      {
        _sessions.TryRemove(sessionId, out _);
      }
    }
    context.Response.Cookies.Delete(_settings.Auth.CookieName, new CookieOptions
    {
      HttpOnly = true,
      Secure = true,
      SameSite = SameSiteMode.Lax,
      Path = "/"
    });
  }

  /// <summary>
  /// Builds the clientPrincipal object in EasyAuth format from a session.
  /// </summary>
  public object BuildClientPrincipal(SessionData session)
  {
    var roles = new List<string>(session.Roles);
    if (!roles.Contains("anonymous")) roles.Add("anonymous");
    if (!roles.Contains("authenticated")) roles.Add("authenticated");

    return new
    {
      identityProvider = "aad",
      userId = session.ObjectId,
      userDetails = session.Upn,
      userRoles = roles.ToArray()
    };
  }

  /// <summary>
  /// Builds the base64-encoded x-ms-client-principal header value.
  /// </summary>
  public string BuildClientPrincipalHeader(SessionData session)
  {
    var principal = BuildClientPrincipal(session);
    var json = JsonSerializer.Serialize(principal);
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
  }

  /// <summary>
  /// Builds the /.auth/me response in Azure SWA EasyAuth format.
  /// </summary>
  public object BuildAuthMeResponse(SessionData session)
  {
    var claims = new List<object>
        {
            new { typ = "aud", val = ClientId },
            new { typ = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", val = session.Upn },
            new { typ = "name", val = session.Name },
            new { typ = "http://schemas.microsoft.com/identity/claims/objectidentifier", val = session.ObjectId },
            new { typ = "preferred_username", val = session.Upn },
            new { typ = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", val = session.ObjectId },
            new { typ = "http://schemas.microsoft.com/identity/claims/tenantid", val = session.TenantId },
            new { typ = "ver", val = "2.0" }
        };

    return new[]
    {
            new
            {
                access_token = session.AccessToken,
                expires_on = session.ExpiresOn.ToString("O"),
                id_token = session.IdToken,
                provider_name = "aad",
                user_claims = claims,
                user_id = session.Upn
            }
        };
  }

  // --- Cookie Encryption ---

  private string EncryptSessionId(string sessionId)
  {
    using var aes = Aes.Create();
    aes.Key = CookieKey;
    aes.GenerateIV();

    using var encryptor = aes.CreateEncryptor();
    var plainBytes = Encoding.UTF8.GetBytes(sessionId);
    var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

    // Prepend IV to ciphertext
    var result = new byte[aes.IV.Length + cipherBytes.Length];
    Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
    Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

    return Convert.ToBase64String(result);
  }

  private string? DecryptSessionId(string encrypted)
  {
    try
    {
      var data = Convert.FromBase64String(encrypted);
      if (data.Length < 17) return null; // IV (16) + at least 1 byte

      using var aes = Aes.Create();
      aes.Key = CookieKey;
      aes.IV = data[..16];

      using var decryptor = aes.CreateDecryptor();
      var plainBytes = decryptor.TransformFinalBlock(data, 16, data.Length - 16);
      return Encoding.UTF8.GetString(plainBytes);
    }
    catch
    {
      return null;
    }
  }

  // --- Data Models ---

  public class SessionData
  {
    public string Upn { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string[] Roles { get; set; } = Array.Empty<string>();
    public string AccessToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public DateTime ExpiresOn { get; set; }
    public DateTime CreatedAt { get; set; }
  }

  private class AllowedUser
  {
    public string Upn { get; set; } = "";
    public string[] Roles { get; set; } = Array.Empty<string>();
  }

  private class TokenResponse
  {
    public string AccessToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public DateTime ExpiresOn { get; set; }
  }
}
