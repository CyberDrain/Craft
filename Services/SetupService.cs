using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;

namespace Craft.Services;

/// <summary>
/// Handles the first-run bootstrap setup:
///   1. PKCE token exchange (auth code → access token, no client secret)
///   2. EasyAuth app registration creation (with secret + exemption policy)
///   3. App Service self-configuration via ARM (authsettingsV2 + env vars)
///
/// All Graph and ARM calls use bearer tokens — either the user's access token
/// (for Graph operations during setup) or the managed identity token (for ARM self-config).
/// </summary>
public class SetupService
{
    private readonly ILogger<SetupService> _logger;
    private readonly CraftSettings _settings;
    private static readonly HttpClient s_httpClient;

    // Retry settings for policy propagation
    private const int MaxPolicyRetries = 6;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    ];

    static SetupService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };
        s_httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        s_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Craft-Setup/1.0");
    }

    public SetupService(ILogger<SetupService> logger, CraftSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Check whether EasyAuth is fully configured by inspecting environment variables.
    /// </summary>
    public static bool IsEasyAuthConfigured()
    {
        var authEnabled = Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED");
        return string.Equals(authEnabled, "True", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the display name for the EasyAuth app registration.
    /// Uses Setup.AuthAppDisplayName if set, otherwise "Craft-EasyAuth-{App.Name}".
    /// </summary>
    public string ResolveAuthAppDisplayName()
    {
        if (!string.IsNullOrEmpty(_settings.Setup.AuthAppDisplayName))
            return _settings.Setup.AuthAppDisplayName;
        return $"Craft-EasyAuth-{_settings.Name}";
    }

    // ── Device Code Flow ──

    /// <summary>
    /// Initiates a device code flow. Returns the user_code and verification_uri
    /// for the user to authenticate at microsoft.com/devicelogin.
    /// </summary>
    public async Task<DeviceCodeResponse> StartDeviceCodeFlow(CancellationToken ct = default)
    {
        var deviceCodeEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _settings.Setup.BootstrapClientId,
            ["scope"] = "https://graph.microsoft.com/Application.ReadWrite.All offline_access openid profile"
        });

        var response = await s_httpClient.PostAsync(deviceCodeEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Setup] Device code request failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Device code request failed: {response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new DeviceCodeResponse
        {
            DeviceCode = root.GetProperty("device_code").GetString()!,
            UserCode = root.GetProperty("user_code").GetString()!,
            VerificationUri = root.GetProperty("verification_uri").GetString()!,
            ExpiresIn = root.GetProperty("expires_in").GetInt32(),
            Interval = root.GetProperty("interval").GetInt32(),
            Message = root.GetProperty("message").GetString()!
        };
    }

    /// <summary>
    /// Polls for device code flow completion. Returns the access token once the
    /// user has authenticated, or null if still pending.
    /// </summary>
    public async Task<TokenExchangeResult?> PollDeviceCodeFlow(string deviceCode, CancellationToken ct = default)
    {
        var tokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _settings.Setup.BootstrapClientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode
        });

        var response = await s_httpClient.PostAsync(tokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!response.IsSuccessStatusCode)
        {
            // "authorization_pending" means user hasn't authenticated yet — normal, keep polling
            if (root.TryGetProperty("error", out var error))
            {
                var errorCode = error.GetString();
                if (errorCode == "authorization_pending")
                    return null; // Still waiting
                if (errorCode == "slow_down")
                    return null; // Too fast, caller should back off
                if (errorCode == "expired_token")
                    throw new InvalidOperationException("Device code expired. Please start setup again.");
            }
            _logger.LogError("[Setup] Device code poll failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Device code authentication failed: {body}");
        }

        var accessToken = root.GetProperty("access_token").GetString()!;
        var tenantId = ExtractTenantIdFromToken(accessToken);

        _logger.LogInformation("[Setup] Device code authentication successful for tenant {TenantId}", tenantId);

        return new TokenExchangeResult
        {
            AccessToken = accessToken,
            TenantId = tenantId
        };
    }

    // ── App Registration Creation ──

    /// <summary>
    /// Creates (or updates) the EasyAuth app registration with a client secret.
    /// Handles app management policy exemption if the tenant blocks password creation.
    /// </summary>
    public async Task<AppRegistrationResult> CreateAuthAppRegistration(
        string accessToken, string tenantId, string redirectUri, bool multiTenant = false, CancellationToken ct = default)
    {
        var authHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Content-Type"] = "application/json"
        };

        var appDisplayName = ResolveAuthAppDisplayName();
        var callbackUri = redirectUri.TrimEnd('/') + "/.auth/login/aad/callback";

        // 1. Check for existing app
        var existingApp = await FindExistingApp(accessToken, appDisplayName, ct);

        string appObjectId;
        string appId;

        if (existingApp != null)
        {
            appObjectId = existingApp.Value.GetProperty("id").GetString()!;
            appId = existingApp.Value.GetProperty("appId").GetString()!;
            _logger.LogInformation("[Setup] Found existing app registration: {AppId}", appId);

            // Update redirect URIs
            var patchBody = new
            {
                web = new
                {
                    redirectUris = new[] { callbackUri },
                    implicitGrantSettings = new { enableIdTokenIssuance = true }
                }
            };
            await GraphRequest(HttpMethod.Patch,
                $"https://graph.microsoft.com/v1.0/applications/{appObjectId}",
                accessToken, patchBody, ct);
        }
        else
        {
            // 2. Create new app registration
            _logger.LogInformation("[Setup] Creating new app registration: {Name}", appDisplayName);

            var createBody = new
            {
                displayName = appDisplayName,
                signInAudience = multiTenant ? "AzureADMultipleOrgs" : "AzureADMyOrg",
                web = new
                {
                    redirectUris = new[] { callbackUri },
                    implicitGrantSettings = new { enableIdTokenIssuance = true }
                }
            };

            var createResponse = await GraphRequest(HttpMethod.Post,
                "https://graph.microsoft.com/v1.0/applications",
                accessToken, createBody, ct);

            appObjectId = createResponse.GetProperty("id").GetString()!;
            appId = createResponse.GetProperty("appId").GetString()!;

            _logger.LogInformation("[Setup] Created app registration: {AppId} (objectId: {ObjId})", appId, appObjectId);

            // 3. Create service principal
            await CreateServicePrincipalSafe(accessToken, appId, ct);

            // Wait briefly for replication
            await Task.Delay(2000, ct);
        }

        // 4. Handle app management policy exemption (must happen before addPassword)
        await EnsurePolicyExemption(accessToken, appId, appObjectId, ct);

        // 5. Create client secret with retry logic
        var secret = await CreateAppSecretWithRetry(accessToken, appObjectId, appDisplayName, ct);

        _logger.LogInformation("[Setup] App registration complete: {AppId}", appId);

        return new AppRegistrationResult
        {
            AppId = appId,
            AppObjectId = appObjectId,
            ClientSecret = secret,
            TenantId = tenantId,
            DisplayName = appDisplayName
        };
    }

    // ── App Management Policy Exemption ──

    /// <summary>
    /// Checks if the tenant's default app management policy blocks credential creation.
    /// If it does, creates or assigns a "{Name} Exemption Policy" for the app.
    /// Mirrors the Update-AppManagementPolicy pattern from PowerShell.
    /// </summary>
    private async Task EnsurePolicyExemption(
        string accessToken, string appId, string appObjectId, CancellationToken ct)
    {
        try
        {
            // Fetch default policy and existing policies in parallel
            var defaultPolicyTask = GraphRequestSafe(HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/policies/defaultAppManagementPolicy",
                accessToken, ct: ct);
            var appPoliciesTask = GraphRequestSafe(HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/policies/appManagementPolicies",
                accessToken, ct: ct);

            await Task.WhenAll(defaultPolicyTask, appPoliciesTask);

            var defaultPolicy = defaultPolicyTask.Result;
            var appPoliciesResponse = appPoliciesTask.Result;

            if (defaultPolicy == null)
            {
                _logger.LogDebug("[Setup] No default app management policy found — skipping exemption check");
                return;
            }

            // Check if default policy blocks passwordAddition or symmetricKeyAddition
            if (!DefaultPolicyBlocksCredentials(defaultPolicy.Value))
            {
                _logger.LogDebug("[Setup] Default policy does not block credentials — no exemption needed");
                return;
            }

            _logger.LogInformation("[Setup] Default policy blocks credential creation — checking for exemption");

            // Get existing app management policies
            var appPolicies = appPoliciesResponse?.GetProperty("value").EnumerateArray().ToList()
                              ?? [];

            // Check if app already has an exemption
            var appAlreadyExempt = false;
            string? existingPolicyId = null;

            foreach (var policy in appPolicies)
            {
                var policyId = policy.GetProperty("id").GetString()!;
                var appliesTo = await GraphRequestSafe(HttpMethod.Get,
                    $"https://graph.microsoft.com/v1.0/policies/appManagementPolicies/{policyId}/appliesTo",
                    accessToken, ct: ct);

                if (appliesTo != null)
                {
                    var targets = appliesTo.Value.GetProperty("value").EnumerateArray();
                    if (targets.Any(t => t.TryGetProperty("appId", out var aid) &&
                                        string.Equals(aid.GetString(), appId, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Check if this policy allows credentials
                        if (!PolicyBlocksCredentials(policy))
                        {
                            appAlreadyExempt = true;
                            _logger.LogInformation("[Setup] App already has credential exemption via policy {PolicyId}", policyId);
                            break;
                        }
                        existingPolicyId = policyId;
                    }
                }
            }

            if (appAlreadyExempt) return;

            // Find or create exemption policy
            var exemptionPolicyName = $"{_settings.Name} Exemption Policy";
            var exemptionPolicy = appPolicies.FirstOrDefault(p =>
                string.Equals(p.GetProperty("displayName").GetString(),
                    exemptionPolicyName, StringComparison.OrdinalIgnoreCase));
            var hasExemptionPolicy = exemptionPolicy.ValueKind != System.Text.Json.JsonValueKind.Undefined;

            var policyBody = new
            {
                displayName = exemptionPolicyName,
                description = $"Allows {_settings.Name}-managed apps to manage credentials",
                isEnabled = true,
                restrictions = new
                {
                    passwordCredentials = new[]
                    {
                        new
                        {
                            restrictionType = "passwordAddition",
                            state = "disabled",
                            restrictForAppsCreatedAfterDateTime = "0001-01-01T00:00:00Z"
                        },
                        new
                        {
                            restrictionType = "symmetricKeyAddition",
                            state = "disabled",
                            restrictForAppsCreatedAfterDateTime = "0001-01-01T00:00:00Z"
                        }
                    },
                    keyCredentials = Array.Empty<object>()
                }
            };

            string targetPolicyId;

            if (existingPolicyId != null)
            {
                // Update existing policy that's already assigned to the app
                await GraphRequest(HttpMethod.Patch,
                    $"https://graph.microsoft.com/v1.0/policies/appManagementPolicies/{existingPolicyId}",
                    accessToken, policyBody, ct);
                _logger.LogInformation("[Setup] Updated existing policy {PolicyId} to allow credentials", existingPolicyId);
                return; // Already assigned
            }
            else if (hasExemptionPolicy)
            {
                // Update and assign existing exemption policy
                targetPolicyId = exemptionPolicy.GetProperty("id").GetString()!;
                await GraphRequest(HttpMethod.Patch,
                    $"https://graph.microsoft.com/v1.0/policies/appManagementPolicies/{targetPolicyId}",
                    accessToken, policyBody, ct);
                _logger.LogInformation("[Setup] Updated existing exemption policy {PolicyId}", targetPolicyId);
            }
            else
            {
                // Create new exemption policy
                var created = await GraphRequest(HttpMethod.Post,
                    "https://graph.microsoft.com/v1.0/policies/appManagementPolicies",
                    accessToken, policyBody, ct);
                targetPolicyId = created.GetProperty("id").GetString()!;
                _logger.LogInformation("[Setup] Created new exemption policy {PolicyId}", targetPolicyId);
            }

            // Assign policy to our app (beta endpoint required for $ref)
            var assignBody = new Dictionary<string, string>
            {
                ["@odata.id"] = $"https://graph.microsoft.com/beta/policies/appManagementPolicies/{targetPolicyId}"
            };
            await GraphRequest(HttpMethod.Post,
                $"https://graph.microsoft.com/beta/applications/{appObjectId}/appManagementPolicies/$ref",
                accessToken, assignBody, ct);

            _logger.LogInformation("[Setup] Assigned exemption policy {PolicyId} to app {AppId}", targetPolicyId, appId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Setup] Failed to set up policy exemption — will attempt secret creation anyway");
        }
    }

    /// <summary>
    /// Creates an app secret with retry logic for policy propagation delays.
    /// </summary>
    private async Task<string> CreateAppSecretWithRetry(
        string accessToken, string appObjectId, string displayName, CancellationToken ct)
    {
        var passwordBody = new
        {
            passwordCredential = new
            {
                displayName = $"{displayName}-Secret"
            }
        };

        for (int attempt = 0; attempt < MaxPolicyRetries; attempt++)
        {
            try
            {
                var result = await GraphRequest(HttpMethod.Post,
                    $"https://graph.microsoft.com/v1.0/applications/{appObjectId}/addPassword",
                    accessToken, passwordBody, ct);

                return result.GetProperty("secretText").GetString()!;
            }
            catch (HttpRequestException ex) when (
                ex.Message.Contains("credential type not allowed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("policy", StringComparison.OrdinalIgnoreCase))
            {
                if (attempt >= MaxPolicyRetries - 1) throw;
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                _logger.LogWarning("[Setup] Secret creation blocked by policy, retry {Attempt}/{Max} in {Delay}s",
                    attempt + 1, MaxPolicyRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException("Failed to create app secret after max retries");
    }

    // ── App Service Self-Configuration via ARM ──

    /// <summary>
    /// Configures the App Service with EasyAuth settings using the managed identity.
    /// Sets environment variables and authsettingsV2 via ARM REST API.
    /// </summary>
    public async Task ConfigureAppServiceAuth(
        string appId, string clientSecret, string tenantId, bool multiTenant = false, CancellationToken ct = default)
    {
        var managementToken = await GetManagedIdentityToken("https://management.azure.com/", ct);
        if (managementToken == null)
            throw new InvalidOperationException("Cannot get managed identity token — is the app running in Azure with a managed identity and Contributor role?");

        var subscriptionId = GetSubscriptionId();
        var resourceGroup = GetResourceGroup();
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")
            ?? throw new InvalidOperationException("WEBSITE_SITE_NAME not set — not running in App Service?");

        var baseUri = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/sites/{siteName}";

        // 1. Read current app settings
        var currentSettings = await ArmRequest(HttpMethod.Post,
            $"{baseUri}/config/appsettings/list?api-version=2024-11-01",
            managementToken, ct: ct);

        var mergedSettings = new Dictionary<string, string>();
        if (currentSettings.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                mergedSettings[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        // 2. Store the client secret — either directly or via Key Vault reference
        var kvName = _settings.Setup.KeyVaultName;
        if (!string.IsNullOrEmpty(kvName))
        {
            if (kvName.Equals("auto", StringComparison.OrdinalIgnoreCase))
                kvName = siteName;

            // Store secret in Key Vault via REST API
            var vaultToken = await GetManagedIdentityToken("https://vault.azure.net", ct)
                ?? throw new InvalidOperationException("Cannot get Key Vault token — ensure the managed identity has Secret Set permission on the vault");

            var kvSecretUrl = $"https://{kvName}.vault.azure.net/secrets/AUTH-SECRET?api-version=7.4";
            var kvBody = new { value = clientSecret };
            await KeyVaultRequest(HttpMethod.Put, kvSecretUrl, vaultToken, kvBody, ct);

            // Set app setting as a KV reference
            mergedSettings["AUTH_SECRET"] = $"@Microsoft.KeyVault(VaultName={kvName};SecretName=AUTH-SECRET)";
            _logger.LogInformation("[Setup] Client secret stored in Key Vault '{VaultName}', app setting set as KV reference", kvName);
        }
        else
        {
            // Store secret directly in app setting (default)
            mergedSettings["AUTH_SECRET"] = clientSecret;
        }

        // Determine effective allowed tenants (always include the setup tenant)
        bool useCommonIssuer = multiTenant;

        // Always remove WEBSITE_AUTH_AAD_ALLOWED_TENANTS — we rely on the issuer URL
        // for tenant restriction ("Use default restrictions based on issuer" in the portal).
        // Multi-tenant uses common/v2.0 issuer, single-tenant uses {tenantId}/v2.0.
        mergedSettings.Remove("WEBSITE_AUTH_AAD_ALLOWED_TENANTS");

        // 3. PUT merged settings back
        var settingsBody = new { properties = mergedSettings };
        await ArmRequest(HttpMethod.Put,
            $"{baseUri}/config/appsettings?api-version=2024-11-01",
            managementToken, settingsBody, ct);

        _logger.LogInformation("[Setup] App settings updated (WEBSITE_AUTH_AAD_ALLOWED_TENANTS removed)");

        // 4. Configure authsettingsV2
        var globalValidation = new Dictionary<string, object>
        {
            ["unauthenticatedClientAction"] = _settings.Setup.UnauthenticatedClientAction,
            ["redirectToProvider"] = "azureactivedirectory"
        };

        if (_settings.Setup.ExcludedPaths.Count > 0)
        {
            globalValidation["excludedPaths"] = _settings.Setup.ExcludedPaths;
            _logger.LogInformation("[Setup] Excluded paths: {Paths}", string.Join(", ", _settings.Setup.ExcludedPaths));
        }

        // Build allowed audiences: always include api://{appId}, plus any extras from config
        var audiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"api://{appId}" };
        foreach (var aud in _settings.Setup.AllowedAudiences)
        {
            if (!string.IsNullOrWhiteSpace(aud))
                audiences.Add(aud);
        }

        var aadValidation = new Dictionary<string, object>
        {
            ["allowedAudiences"] = audiences.ToArray()
        };

        // Always restrict to specific client applications — include the app's own ID
        // plus any extras from config. This sets the "Client application requirement"
        // to "Allow requests from specific client applications" in EasyAuth.
        var allowedApps = new HashSet<string>(_settings.Setup.AllowedApplications, StringComparer.OrdinalIgnoreCase) { appId };
        aadValidation["defaultAuthorizationPolicy"] = new
        {
            allowedPrincipals = new { },
            allowedApplications = allowedApps.ToArray()
        };
        _logger.LogInformation("[Setup] Allowed client applications: {Apps}", string.Join(", ", allowedApps));

        var authConfig = new
        {
            properties = new
            {
                platform = new { enabled = true },
                globalValidation,
                identityProviders = new
                {
                    azureActiveDirectory = new
                    {
                        enabled = true,
                        registration = new
                        {
                            clientId = appId,
                            clientSecretSettingName = "AUTH_SECRET",
                            openIdIssuer = useCommonIssuer
                                ? "https://login.microsoftonline.com/common/v2.0"
                                : $"https://login.microsoftonline.com/{tenantId}/v2.0"
                        },
                        validation = aadValidation
                    }
                },
                login = new
                {
                    tokenStore = new
                    {
                        enabled = true,
                        tokenRefreshExtensionHours = 72
                    }
                }
            }
        };

        await ArmRequest(HttpMethod.Put,
            $"{baseUri}/config/authsettingsV2?api-version=2020-06-01",
            managementToken, authConfig, ct);

        _logger.LogInformation("[Setup] authsettingsV2 configured for app {AppId}", appId);
    }

    /// <summary>
    /// Saves app registration details manually (user-provided App ID, Secret, Tenant ID)
    /// and configures the App Service via ARM.
    /// </summary>
    public async Task ConfigureManual(
        string appId, string clientSecret, string tenantId, bool multiTenant = false, CancellationToken ct = default)
    {
        await ConfigureAppServiceAuth(appId, clientSecret, tenantId, multiTenant, ct);
    }

    // ── First User Seeding ──

    /// <summary>
    /// Resolves the storage connection string for the allowedUsers table.
    /// Same logic as AuthService — uses Auth.UserStorageConnection if set,
    /// falls back to AzureWebJobsStorage, then dev storage.
    /// </summary>
    private string StorageConnectionString =>
        (!string.IsNullOrEmpty(_settings.Auth.UserStorageConnection)
            ? _settings.Auth.UserStorageConnection
            : Environment.GetEnvironmentVariable("AzureWebJobsStorage"))
        ?? "UseDevelopmentStorage=true";

    /// <summary>
    /// Resolves the user table name with the same sanitization as AuthService.
    /// </summary>
    private string ResolveUserTableName()
    {
        var raw = _settings.Auth.UserTableName;
        var sanitized = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        if (sanitized.Length > 63) sanitized = sanitized[..63];
        if (sanitized.Length < 3) sanitized = "allowedUsers";
        return sanitized;
    }

    /// <summary>
    /// Checks the allowedUsers table status: whether it's reachable and whether
    /// it already contains any users.
    /// </summary>
    public async Task<AllowedUsersStatus> CheckAllowedUsersStatus(CancellationToken ct = default)
    {
        try
        {
            var tableName = ResolveUserTableName();
            var client = new TableClient(StorageConnectionString, tableName);
            await client.CreateIfNotExistsAsync(cancellationToken: ct);

            var count = 0;
            await foreach (var entity in client.QueryAsync<TableEntity>(cancellationToken: ct))
            {
                if (!entity.RowKey.StartsWith("_"))
                {
                    count++;
                    if (count > 0) break; // We only need to know if any exist
                }
            }

            return new AllowedUsersStatus
            {
                Connected = true,
                HasUsers = count > 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Setup] Failed to check allowedUsers table");
            return new AllowedUsersStatus
            {
                Connected = false,
                HasUsers = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Seeds the first superadmin user into the allowedUsers table.
    /// Only works when the table is empty — refuses if users already exist.
    /// Uses the same entity schema as CIPP-API's Invoke-ExecCIPPUsers.
    /// </summary>
    public async Task SeedFirstUser(string upn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
            throw new ArgumentException("UPN (email) is required.");

        upn = upn.Trim().ToLower();

        var tableName = ResolveUserTableName();
        var client = new TableClient(StorageConnectionString, tableName);
        await client.CreateIfNotExistsAsync(cancellationToken: ct);

        // Guard: refuse if the table already has users
        await foreach (var entity in client.QueryAsync<TableEntity>(cancellationToken: ct))
        {
            if (!entity.RowKey.StartsWith("_"))
                throw new InvalidOperationException("The allowed users table already contains users. First-user seeding is only available on an empty table.");
        }

        var roles = new[] { "superadmin" };
        var rolesJson = JsonSerializer.Serialize(roles);

        var userEntity = new TableEntity("User", upn)
        {
            ["Roles"] = rolesJson,
            ["ManualRoles"] = rolesJson,
            ["AutoRoles"] = "[]",
            ["Source"] = "Manual"
        };

        await client.UpsertEntityAsync(userEntity, TableUpdateMode.Replace, ct);
        _logger.LogInformation("[Setup] Seeded first superadmin user: {Upn}", upn);
    }

    // ── Status ──

    /// <summary>
    /// Returns setup status information.
    /// </summary>
    public async Task<SetupStatus> GetStatus(CancellationToken ct = default)
    {
        var isConfigured = IsEasyAuthConfigured();
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        var hasManagedIdentity = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"));
        var usersStatus = await CheckAllowedUsersStatus(ct);

        return new SetupStatus
        {
            IsEasyAuthConfigured = isConfigured,
            IsSetupCompleted = AppLifecycleBridge.IsSetupCompleted(),
            SetupCompletedReason = AppLifecycleBridge.GetSetupCompletedReason(),
            IsRunningInAppService = !string.IsNullOrEmpty(siteName),
            HasManagedIdentity = hasManagedIdentity,
            AppName = _settings.Name,
            AuthAppDisplayName = ResolveAuthAppDisplayName(),
            BootstrapClientId = _settings.Setup.BootstrapClientId,
            UsersStatus = usersStatus
        };
    }

    // ── Helper Methods ──

    private async Task<JsonElement?> FindExistingApp(string accessToken, string displayName, CancellationToken ct)
    {
        var encodedName = Uri.EscapeDataString(displayName);
        var result = await GraphRequestSafe(HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/applications?$filter=displayName eq '{encodedName}'",
            accessToken, ct: ct);

        if (result == null) return null;

        var values = result.Value.GetProperty("value").EnumerateArray().ToList();
        return values.Count > 0 ? values[^1] : null;
    }

    private async Task CreateServicePrincipalSafe(string accessToken, string appId, CancellationToken ct)
    {
        try
        {
            var body = new { appId };
            await GraphRequest(HttpMethod.Post,
                "https://graph.microsoft.com/v1.0/servicePrincipals",
                accessToken, body, ct);
            _logger.LogDebug("[Setup] Created service principal for {AppId}", appId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Setup] Service principal creation failed (may already exist) for {AppId}", appId);
        }
    }

    private static bool DefaultPolicyBlocksCredentials(JsonElement defaultPolicy)
    {
        if (!defaultPolicy.TryGetProperty("applicationRestrictions", out var restrictions))
            return false;
        if (!restrictions.TryGetProperty("passwordCredentials", out var pwdCreds))
            return false;

        foreach (var restriction in pwdCreds.EnumerateArray())
        {
            if (restriction.TryGetProperty("state", out var state) &&
                string.Equals(state.GetString(), "enabled", StringComparison.OrdinalIgnoreCase) &&
                restriction.TryGetProperty("restrictionType", out var rType))
            {
                var type = rType.GetString();
                if (type is "passwordAddition" or "symmetricKeyAddition")
                    return true;
            }
        }
        return false;
    }

    private static bool PolicyBlocksCredentials(JsonElement policy)
    {
        if (!policy.TryGetProperty("restrictions", out var restrictions))
            return false;
        if (!restrictions.TryGetProperty("passwordCredentials", out var pwdCreds))
            return false;

        foreach (var restriction in pwdCreds.EnumerateArray())
        {
            if (restriction.TryGetProperty("state", out var state) &&
                string.Equals(state.GetString(), "enabled", StringComparison.OrdinalIgnoreCase) &&
                restriction.TryGetProperty("restrictionType", out var rType))
            {
                var type = rType.GetString();
                if (type is "passwordAddition" or "symmetricKeyAddition")
                    return true;
            }
        }
        return false;
    }

    private static string ExtractTenantIdFromToken(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) throw new InvalidOperationException("Invalid JWT");

        var payload = parts[1];
        // Fix base64 padding
        payload = payload.Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("tid").GetString()
               ?? throw new InvalidOperationException("Token missing tid claim");
    }

    private static string GetSubscriptionId()
    {
        // Azure sets WEBSITE_OWNER_NAME = "{subscriptionId}+{resourceGroup}-{region}webspace..."
        var ownerName = Environment.GetEnvironmentVariable("WEBSITE_OWNER_NAME")
            ?? throw new InvalidOperationException("WEBSITE_OWNER_NAME not set — not in App Service?");
        var plusIdx = ownerName.IndexOf('+');
        return plusIdx > 0 ? ownerName[..plusIdx] : ownerName;
    }

    private static string GetResourceGroup()
    {
        return Environment.GetEnvironmentVariable("WEBSITE_RESOURCE_GROUP")
            ?? throw new InvalidOperationException("WEBSITE_RESOURCE_GROUP not set — not in App Service?");
    }

    private async Task<string?> GetManagedIdentityToken(string resource, CancellationToken ct)
    {
        var identityEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var identityHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");

        if (string.IsNullOrEmpty(identityEndpoint) || string.IsNullOrEmpty(identityHeader))
            return null;

        var url = $"{identityEndpoint}?api-version=2019-08-01&resource={Uri.EscapeDataString(resource)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-IDENTITY-HEADER", identityHeader);

        var response = await s_httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Setup] Managed identity token request failed: {Status} {Body}", response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    // ── HTTP Request Helpers ──

    private static async Task<JsonElement> GraphRequest(
        HttpMethod method, string url, string accessToken, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var response = await s_httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Graph API {method} {url} failed: {response.StatusCode} — {responseBody}");
        }

        if (string.IsNullOrWhiteSpace(responseBody))
            return default;

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement?> GraphRequestSafe(
        HttpMethod method, string url, string accessToken, object? body = null, CancellationToken ct = default)
    {
        try
        {
            return await GraphRequest(method, url, accessToken, body, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonElement> ArmRequest(
        HttpMethod method, string url, string accessToken, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var response = await s_httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Setup] ARM {Method} {Url} failed: {Status} {Body}",
                method, url, response.StatusCode, responseBody);
            throw new HttpRequestException($"ARM {method} {url} failed: {response.StatusCode}");
        }

        if (string.IsNullOrWhiteSpace(responseBody))
            return default;

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.Clone();
    }

    private async Task KeyVaultRequest(
        HttpMethod method, string url, string accessToken, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await s_httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[Setup] Key Vault {Method} {Url} failed: {Status} {Body}",
                method, url, response.StatusCode, responseBody);
            throw new HttpRequestException($"Key Vault {method} {url} failed: {response.StatusCode}");
        }
    }

    // ── Result Models ──

    public class TokenExchangeResult
    {
        public string AccessToken { get; set; } = "";
        public string TenantId { get; set; } = "";
    }

    public class AppRegistrationResult
    {
        public string AppId { get; set; } = "";
        public string AppObjectId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public class SetupStatus
    {
        public bool IsEasyAuthConfigured { get; set; }
        public bool IsSetupCompleted { get; set; }
        public string? SetupCompletedReason { get; set; }
        public bool IsRunningInAppService { get; set; }
        public bool HasManagedIdentity { get; set; }
        public string AppName { get; set; } = "";
        public string AuthAppDisplayName { get; set; } = "";
        public string BootstrapClientId { get; set; } = "";
        public AllowedUsersStatus UsersStatus { get; set; } = new();
    }

    public class AllowedUsersStatus
    {
        public bool Connected { get; set; }
        public bool HasUsers { get; set; }
        public string? Error { get; set; }
    }

    public class DeviceCodeResponse
    {
        public string DeviceCode { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
        public string Message { get; set; } = "";
    }
}
