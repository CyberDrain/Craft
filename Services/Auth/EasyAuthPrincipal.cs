using System.Text;
using System.Text.Json;

namespace Craft.Auth;

/// <summary>
/// The identity claims CRAFT cares about, pulled out of an App Service EasyAuth principal.
/// </summary>
/// <param name="Upn">
/// Authoritative user principal name, or <see langword="null"/> for an app-only token.
/// </param>
/// <param name="ObjectId">Entra object id of the user or service principal.</param>
/// <param name="AppId">Client application id, present on client-credentials tokens.</param>
/// <param name="IdentityType">The <c>idtyp</c> claim — <c>"app"</c> for app-only tokens.</param>
public sealed record EasyAuthClaims(string? Upn, string? ObjectId, string? AppId, string? IdentityType)
{
    /// <summary>
    /// Whether this is an app-only (client-credentials) token rather than a signed-in user.
    /// </summary>
    /// <remarks>
    /// The distinction decides which authorization path runs: a user is checked against the
    /// allowedUsers table, a service principal is not. Getting it wrong either locks out every API
    /// client or lets an unlisted user through, so it keys off the absence of a UPN plus positive
    /// evidence of an app identity.
    /// </remarks>
    public bool IsAppOnly =>
        string.IsNullOrEmpty(Upn) &&
        (!string.IsNullOrEmpty(AppId) || string.Equals(IdentityType, "app", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Translation between the App Service EasyAuth principal format and the Static Web Apps format the
/// hosted PowerShell application expects.
/// </summary>
public static class EasyAuthPrincipal
{
    /// <summary>
    /// Paths that never get principal-header injection: static assets and the platform's own auth
    /// routes. Skipping them keeps an allowedUsers lookup off the asset path.
    /// </summary>
    public static bool ShouldSkipInjection(string path) =>
        path.StartsWith("/_next/", StringComparison.Ordinal) ||
        path.StartsWith("/assets/", StringComparison.Ordinal) ||
        path.StartsWith("/.auth/", StringComparison.Ordinal) ||
        Path.HasExtension(path);

    /// <summary>
    /// Whether a decoded principal is in EasyAuth format and so needs transforming.
    /// </summary>
    /// <remarks>
    /// EasyAuth emits a <c>claims</c> array; the SWA format emits <c>userRoles</c>. A principal that
    /// already has <c>userRoles</c> came from the trusted front end and passes through untouched —
    /// EasyAuth strips inbound principal headers upstream, so it cannot have been spoofed by a caller.
    /// </remarks>
    public static bool NeedsTransform(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("claims", out _) &&
        !root.TryGetProperty("userRoles", out _);

    /// <summary>Decodes a base64 <c>x-ms-client-principal</c> header value.</summary>
    /// <exception cref="FormatException">Not valid base64.</exception>
    /// <exception cref="JsonException">Not valid JSON.</exception>
    public static JsonDocument Decode(string headerValue) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(headerValue)));

    /// <summary>Encodes a principal object back into a base64 header value.</summary>
    public static string Encode<T>(T principal) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(principal)));

    /// <summary>
    /// Pulls the identity claims out of an EasyAuth principal.
    /// </summary>
    public static EasyAuthClaims ExtractClaims(JsonElement root)
    {
        string? upn = null, preferredUsername = null, oid = null, appId = null, idtyp = null;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("claims", out var claims) &&
            claims.ValueKind == JsonValueKind.Array)
        {
            foreach (var claim in claims.EnumerateArray())
            {
                if (!claim.TryGetProperty("typ", out var typEl)) continue;

                var typ = typEl.GetString() ?? "";
                var val = claim.TryGetProperty("val", out var valEl) ? valEl.GetString() ?? "" : "";

                switch (typ)
                {
                    case "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn":
                    case "upn":
                        upn ??= val;
                        break;
                    case "preferred_username":
                        preferredUsername ??= val;
                        break;
                    case "http://schemas.microsoft.com/identity/claims/objectidentifier":
                        oid ??= val;
                        break;
                    case "appid":
                    case "azp":
                        appId ??= val;
                        break;
                    case "idtyp":
                        idtyp ??= val;
                        break;
                    default:
                        break;
                }
            }
        }

        // preferred_username is a fallback only. It is user-changeable and not guaranteed unique, so an
        // authoritative upn claim always wins when one is present.
        upn = string.IsNullOrEmpty(upn) ? preferredUsername : upn;

        return new EasyAuthClaims(
            string.IsNullOrEmpty(upn) ? null : upn,
            string.IsNullOrEmpty(oid) ? null : oid,
            string.IsNullOrEmpty(appId) ? null : appId,
            string.IsNullOrEmpty(idtyp) ? null : idtyp);
    }

    /// <summary>
    /// Resolves the real identity provider: the platform header if present, else the principal's own
    /// <c>auth_typ</c>.
    /// </summary>
    public static string ResolveIdentityProvider(string? idpHeader, JsonElement root)
    {
        if (!string.IsNullOrEmpty(idpHeader)) return idpHeader;

        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("auth_typ", out var authTyp)
            ? authTyp.GetString() ?? ""
            : "";
    }
}
