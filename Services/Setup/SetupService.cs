using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Craft.Configuration;
using Craft.Services;
using Craft.Storage;

namespace Craft.Setup;

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
    private readonly ICraftTableStore _store;
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

    public SetupService(ILogger<SetupService> logger, CraftSettings settings, ICraftTableStore store)
    {
        _logger = logger;
        _settings = settings;
        _store = store;
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
    /// Creates a new EasyAuth app registration with a client secret. Existing
    /// registrations in the tenant are never searched for or reused.
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

        // 1. Create a new app registration — existing apps are never reused, so we
        // only ever add a secret to a registration this run just created.
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

        var appObjectId = createResponse.GetProperty("id").GetString()!;
        var appId = createResponse.GetProperty("appId").GetString()!;

        _logger.LogInformation("[Setup] Created app registration: {AppId} (objectId: {ObjId})", appId, appObjectId);

        // 2. Create service principal
        await CreateServicePrincipalSafe(accessToken, appId, ct);

        // Wait briefly for replication
        await Task.Delay(2000, ct);

        // 3. Handle app management policy exemption (must happen before addPassword)
        await EnsurePolicyExemption(accessToken, appId, appObjectId, ct);

        // 4. Create client secret with retry logic
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

        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")
            ?? throw new InvalidOperationException("WEBSITE_SITE_NAME not set — not running in App Service?");
        var baseUri = GetArmSiteBaseUri();

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

            // Store secrets in Key Vault via REST API
            var vaultToken = await GetManagedIdentityToken("https://vault.azure.net", ct)
                ?? throw new InvalidOperationException("Cannot get Key Vault token — ensure the managed identity has Secret Set permission on the vault");

            // Secret names are configurable (App:Setup:SsoSecretNames), defaulting to the
            // names CIPP expects: SSOAppSecret / SSOAppId / SSOMultiTenant.
            var secretNames = _settings.Setup.SsoSecretNames;

            // Client secret — this is what the AUTH_SECRET app setting references.
            await PutKeyVaultSecret(kvName, secretNames.AppSecret, clientSecret, vaultToken, ct);
            mergedSettings["AUTH_SECRET"] = $"@Microsoft.KeyVault(VaultName={kvName};SecretName={secretNames.AppSecret})";

            // App (client) ID and multi-tenant flag — persisted so the downstream app can read
            // its own SSO credentials from the same vault.
            await PutKeyVaultSecret(kvName, secretNames.AppId, appId, vaultToken, ct);
            await PutKeyVaultSecret(kvName, secretNames.MultiTenant, multiTenant ? "true" : "false", vaultToken, ct);

            _logger.LogInformation(
                "[Setup] SSO app details stored in Key Vault '{VaultName}' (secrets '{AppSecret}', '{AppId}', '{MultiTenant}'); AUTH_SECRET set as KV reference",
                kvName, secretNames.AppSecret, secretNames.AppId, secretNames.MultiTenant);
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
            ["redirectToProvider"] = _settings.Setup.RedirectToProvider
        };

        var excludedPaths = EffectiveExcludedPaths();
        if (excludedPaths.Count > 0)
        {
            globalValidation["excludedPaths"] = excludedPaths;
            _logger.LogInformation("[Setup] Excluded paths: {Paths}", string.Join(", ", excludedPaths));
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
    /// Reconciles the live authsettingsV2.globalValidation block with current Setup settings.
    /// Reads UnauthenticatedClientAction and ExcludedPaths from CraftSettings and writes only
    /// those fields back via ARM. Identity providers, secrets, audiences, and the AAD validation
    /// block are preserved verbatim.
    ///
    /// Current live config is read from the WEBSITE_AUTH_V2_CONFIG_JSON env var (Azure injects
    /// this with the active authsettingsV2 JSON on container start — same source the platform
    /// shows, no ARM round-trip required to diff). ARM is only touched if a PUT is needed.
    ///
    /// Idempotent — if the live config already matches the desired values, no ARM PUT is issued
    /// and the method returns false. Safe to call on every container warmup.
    ///
    /// Returns true if the live config was changed; false if already in sync, EasyAuth is not
    /// configured, or no managed identity token is available.
    /// </summary>
    public async Task<bool> ReconcileAuthPolicy(string reason, CancellationToken ct = default)
    {
        if (!IsEasyAuthConfigured())
        {
            _logger.LogInformation("[Setup] Reconcile skipped — EasyAuth not configured ({Reason})", reason);
            return false;
        }

        // Read live config from the env var Azure injects on container start.
        // Same JSON that ARM's GET would return; no HTTP call required for the diff.
        var liveJson = Environment.GetEnvironmentVariable("WEBSITE_AUTH_V2_CONFIG_JSON");
        if (string.IsNullOrWhiteSpace(liveJson))
        {
            _logger.LogWarning("[Setup] Reconcile aborted — WEBSITE_AUTH_V2_CONFIG_JSON not set ({Reason})", reason);
            return false;
        }

        JsonElement liveProps;
        try
        {
            using var liveDoc = JsonDocument.Parse(liveJson);
            liveProps = liveDoc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Setup] Reconcile aborted — WEBSITE_AUTH_V2_CONFIG_JSON unparseable ({Reason})", reason);
            return false;
        }

        // Extract current values for diff
        string currentAction = "";
        var currentPaths = new List<string>();
        if (liveProps.TryGetProperty("globalValidation", out var currentGv))
        {
            if (currentGv.TryGetProperty("unauthenticatedClientAction", out var actionEl))
                currentAction = NormalizeUnauthAction(actionEl);
            if (currentGv.TryGetProperty("excludedPaths", out var pathsEl) && pathsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pathsEl.EnumerateArray())
                {
                    var s = p.GetString();
                    if (!string.IsNullOrEmpty(s)) currentPaths.Add(s);
                }
            }
        }

        // Also read current redirectToProvider so we include it in the diff
        var currentProvider = "";
        if (liveProps.TryGetProperty("globalValidation", out var gvForProvider)
            && gvForProvider.TryGetProperty("redirectToProvider", out var providerEl)
            && providerEl.ValueKind == JsonValueKind.String)
        {
            currentProvider = providerEl.GetString() ?? "";
        }

        var desiredAction = _settings.Setup.UnauthenticatedClientAction;
        var desiredPaths = EffectiveExcludedPaths();
        var desiredProvider = _settings.Setup.RedirectToProvider;

        var actionMatches = string.Equals(currentAction, desiredAction, StringComparison.OrdinalIgnoreCase);
        var providerMatches = string.Equals(currentProvider, desiredProvider, StringComparison.OrdinalIgnoreCase);
        var pathsMatch = currentPaths.Count == desiredPaths.Count
            && currentPaths.OrderBy(s => s, StringComparer.Ordinal)
                .SequenceEqual(desiredPaths.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal);

        if (actionMatches && providerMatches && pathsMatch)
        {
            _logger.LogInformation("[Setup] Reconcile: already in sync — action={Action}, provider={Provider}, paths={PathCount} ({Reason})",
                desiredAction, desiredProvider, desiredPaths.Count, reason);
            return false;
        }

        // Drift detected — GET the full authsettingsV2, replace globalValidation, PUT back.
        // The helper preserves identityProviders / secrets / validation untouched.
        var success = await UpdateAuthSettingsV2Async(props =>
        {
            var newGv = new System.Text.Json.Nodes.JsonObject
            {
                ["unauthenticatedClientAction"] = desiredAction,
                ["redirectToProvider"] = desiredProvider
            };
            if (desiredPaths.Count > 0)
            {
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var p in desiredPaths) arr.Add(p);
                newGv["excludedPaths"] = arr;
            }
            // Preserve other globalValidation keys (requireAuthentication, ...)
            if (props["globalValidation"] is System.Text.Json.Nodes.JsonObject existingGv)
            {
                foreach (var kv in existingGv)
                {
                    if (kv.Key == "unauthenticatedClientAction"
                        || kv.Key == "excludedPaths"
                        || kv.Key == "redirectToProvider") continue;
                    newGv[kv.Key] = kv.Value?.DeepClone();
                }
            }
            props["globalValidation"] = newGv;
        }, ct);

        if (!success)
        {
            _logger.LogWarning("[Setup] Reconcile drift detected but PUT failed ({Reason})", reason);
            return false;
        }

        _logger.LogInformation(
            "[Setup] Reconcile applied — action: {OldAction}→{NewAction}, paths: {OldCount}→{NewCount} ({Reason})",
            currentAction, desiredAction, currentPaths.Count, desiredPaths.Count, reason);
        return true;
    }

    /// <summary>
    /// The configured excluded paths plus, when Craft serves protected resource metadata
    /// (App:Prm:Enabled), the well-known PRM path — anonymous discovery requests must reach the
    /// container instead of being redirected/rejected by EasyAuth. Trailing wildcard covers the
    /// path-suffixed variants (RFC 9728 §3). Used by both the initial authsettingsV2 write and
    /// ReconcileAuthPolicy so the two never disagree on the desired state.
    /// </summary>
    private List<string> EffectiveExcludedPaths()
    {
        var paths = new List<string>(_settings.Setup.ExcludedPaths);
        if (_settings.Prm.Enabled
            && !paths.Any(p => p.StartsWith(PrmSettings.WellKnownPath, StringComparison.OrdinalIgnoreCase)))
        {
            paths.Add(PrmSettings.WellKnownPath + "*");
        }
        return paths;
    }

    /// <summary>
    /// Normalizes the unauthenticatedClientAction value as returned by ARM. Azure GETs return
    /// the enum as an integer; PUTs accept both strings and integers. Map to canonical string.
    /// </summary>
    private static string NormalizeUnauthAction(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetInt32() switch
        {
            0 => "RedirectToLoginPage",
            1 => "AllowAnonymous",
            2 => "Return401",
            3 => "Return403",
            _ => el.GetInt32().ToString(CultureInfo.InvariantCulture)
        },
        JsonValueKind.String => el.GetString() ?? "",
        _ => ""
    };

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
            await _store.EnsureTableAsync(tableName, ct);

            var count = 0;
            await foreach (var row in _store.QueryTableAsync(tableName, ct))
            {
                if (!row.RowKey.StartsWith('_'))
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
    /// Seeds the first user into the allowedUsers table with the roles from
    /// Setup.FirstUserRoles (defaults to "superadmin" when unset).
    /// Only works when the table is empty — refuses if users already exist.
    /// Uses the same entity schema as CIPP-API's Invoke-ExecCIPPUsers.
    /// </summary>
    public async Task SeedFirstUser(string upn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
            throw new ArgumentException("UPN (email) is required.");

        // Invariant, not current-culture: this value is an identity key compared against rows
        // written by AuthService (which already lowercases invariantly). Under a Turkish locale
        // the two would disagree on "I"/"i" and a seeded user would fail to match.
        upn = upn.Trim().ToLowerInvariant();

        var tableName = ResolveUserTableName();
        await _store.EnsureTableAsync(tableName, ct);

        // Guard: refuse if the table already has users
        await foreach (var row in _store.QueryTableAsync(tableName, ct))
        {
            if (!row.RowKey.StartsWith('_'))
                throw new InvalidOperationException("The allowed users table already contains users. First-user seeding is only available on an empty table.");
        }

        string[] roles = _settings.Setup.FirstUserRoles.Count > 0
            ? _settings.Setup.FirstUserRoles.ToArray()
            : ["superadmin"];
        var rolesJson = JsonSerializer.Serialize(roles);

        var userRow = new StoreRow("User", upn)
        {
            Properties =
            {
                ["Roles"] = rolesJson,
                ["ManualRoles"] = rolesJson,
                ["AutoRoles"] = "[]",
                ["Source"] = "Manual"
            }
        };

        await _store.UpsertAsync(tableName, userRow, ct);
        _logger.LogInformation("[Setup] Seeded first user {Upn} with roles {Roles}", upn, string.Join(",", roles));
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

    /// <summary>
    /// Build the ARM base URI for the running app's Microsoft.Web/sites resource.
    /// Throws if not running in App Service (WEBSITE_SITE_NAME unset).
    /// </summary>
    private static string GetArmSiteBaseUri()
    {
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")
            ?? throw new InvalidOperationException("WEBSITE_SITE_NAME not set — not running in App Service?");
        return $"https://management.azure.com/subscriptions/{GetSubscriptionId()}"
            + $"/resourceGroups/{GetResourceGroup()}/providers/Microsoft.Web/sites/{siteName}";
    }

    /// <summary>
    /// GET-mutate-PUT for authsettingsV2. Fetches the live config, hands the mutable
    /// properties JsonObject to the caller for surgical edits, then PUTs the result.
    /// All fields the mutator doesn't touch (identity providers, secrets, validation, etc.)
    /// are preserved verbatim.
    ///
    /// Returns true if the PUT succeeded; false if no managed identity token is available
    /// or the response shape is unexpected. Any ARM error inside the PUT throws.
    /// </summary>
    private async Task<bool> UpdateAuthSettingsV2Async(Action<System.Text.Json.Nodes.JsonObject> mutateProperties, CancellationToken ct = default)
    {
        var token = await GetManagedIdentityToken("https://management.azure.com/", ct);
        if (token == null)
        {
            _logger.LogWarning("[Setup] ARM update: no managed identity token");
            return false;
        }

        var uri = $"{GetArmSiteBaseUri()}/config/authsettingsV2?api-version=2020-06-01";
        var current = await ArmRequest(HttpMethod.Get, uri, token, ct: ct);
        if (!current.TryGetProperty("properties", out var props))
        {
            _logger.LogWarning("[Setup] ARM update: authsettingsV2 response missing 'properties'");
            return false;
        }

        var propsNode = System.Text.Json.Nodes.JsonNode.Parse(props.GetRawText())!.AsObject();
        mutateProperties(propsNode);

        var body = new System.Text.Json.Nodes.JsonObject { ["properties"] = propsNode };
        await ArmRequest(HttpMethod.Put, uri, token, body, ct);
        return true;
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

    /// <summary>
    /// Sets (creates or updates) a single Key Vault secret via the vault's REST API.
    /// </summary>
    private Task PutKeyVaultSecret(
        string vaultName, string secretName, string value, string vaultToken, CancellationToken ct)
    {
        var url = $"https://{vaultName}.vault.azure.net/secrets/{secretName}?api-version=7.4";
        return KeyVaultRequest(HttpMethod.Put, url, vaultToken, new { value }, ct);
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
