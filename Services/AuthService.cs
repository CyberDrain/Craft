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
      // Derive a stable key from client secret so sessions survive restarts
      var secret = ClientSecret;
      _cookieKey = SHA256.HashData(Encoding.UTF8.GetBytes(secret + "_craft_session_key"));
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
    if (allowedUser == null)
    {
      _logger.LogWarning("[Auth] User {Upn} not in allowedUsers table — denied", upn);
      throw new UnauthorizedAccessException($"User '{upn}' is not authorized. Contact your administrator.");
    }

    _logger.LogInformation("[Auth] User {Upn} authorized with roles: {Roles}", upn, string.Join(",", allowedUser.Roles));

    // 4. Create session
    var sessionId = Guid.NewGuid().ToString("N");
    var session = new SessionData
    {
      Upn = upn,
      ObjectId = oid,
      Name = name,
      TenantId = tid,
      Roles = allowedUser.Roles,
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
      var client = new TableClient(StorageConnectionString, _settings.Auth.UserTableName);
      await client.CreateIfNotExistsAsync(cancellationToken: ct);

      var newCache = new Dictionary<string, AllowedUser>(StringComparer.OrdinalIgnoreCase);

      await foreach (var entity in client.QueryAsync<TableEntity>(cancellationToken: ct))
      {
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
      Secure = true,
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
