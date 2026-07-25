namespace Craft.Configuration;

/// <summary>
/// Key Vault secret names used by the bootstrap when persisting the created SSO app
/// registration's details. Each is the SecretName portion of the Key Vault secret the
/// setup flow writes (and, for the client secret, the SecretName the AUTH_SECRET app
/// setting references). Defaults mirror CIPP's expected names.
/// </summary>
public class SsoSecretNames
{
    /// <summary>
    /// KV secret name holding the EasyAuth client secret. This is what the AUTH_SECRET app
    /// setting's Key Vault reference points at. Default "SSOAppSecret".
    /// </summary>
    public string AppSecret { get; set; } = "SSOAppSecret";

    /// <summary>KV secret name holding the app (client) ID. Default "SSOAppId".</summary>
    public string AppId { get; set; } = "SSOAppId";

    /// <summary>KV secret name holding the multi-tenant flag ("true"/"false"). Default "SSOMultiTenant".</summary>
    public string MultiTenant { get; set; } = "SSOMultiTenant";
}
