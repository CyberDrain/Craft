using System.Text.Json;
using Craft.Configuration;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Serves the OAuth discovery documents at their well-known paths: Protected Resource Metadata
/// (RFC 9728) at <c>/.well-known/oauth-protected-resource</c>, and — when the hosted app provides
/// one — authorization server metadata (RFC 8414) at <c>/.well-known/oauth-authorization-server</c>
/// plus its OIDC discovery alias. Together these are how MCP clients discover the authorization
/// server, scopes, and (via a <c>registration_endpoint</c> in the AS document) how to obtain a
/// client ID without any manual configuration.
///
/// <para>
/// Every document comes verbatim from one app setting — Craft adds nothing and interprets nothing
/// beyond the per-request <c>{origin}</c> substitution. Metadata only: none of the endpoints named
/// inside the documents are implemented here; authorize/token belong to Entra, registration to the
/// hosted app, and EasyAuth remains the sole token validator. See <see cref="PrmSettings"/> for why
/// this exists instead of the platform's own PRM preview.
/// </para>
/// </summary>
public static class PrmEndpoint
{
    /// <summary>Replaced per-request with <c>https://{host}</c> wherever it appears in the JSON.</summary>
    public const string OriginPlaceholder = "{origin}";

    /// <summary>
    /// Maps the well-known routes for each document whose app setting holds valid JSON. Templates
    /// are read once at startup — app setting changes restart the container.
    /// </summary>
    public static WebApplication MapCraftPrmEndpoint(
        this WebApplication app, CraftSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var getEnv = (Func<string, string?>)Environment.GetEnvironmentVariable;

        var prmTemplate = ResolveTemplate(settings.Prm.SettingName, getEnv, logger);
        if (prmTemplate is not null)
            MapDocument(app, PrmSettings.WellKnownPath, prmTemplate);

        var asTemplate = ResolveTemplate(settings.Prm.AuthServerSettingName, getEnv, logger);
        if (asTemplate is not null)
        {
            // MCP clients are required to try RFC 8414 first and OIDC discovery second; both must
            // answer with the same document for either probe order to succeed.
            MapDocument(app, PrmSettings.AuthServerWellKnownPath, asTemplate);
            MapDocument(app, PrmSettings.OidcWellKnownPath, asTemplate);
        }

        if (prmTemplate is null && asTemplate is null)
        {
            logger.LogInformation(
                "[Prm] Neither app setting '{Prm}' nor '{As}' is set — OAuth discovery documents not served",
                settings.Prm.SettingName, settings.Prm.AuthServerSettingName);
            return app;
        }

        logger.LogInformation(
            "[Prm] Discovery documents: PRM={Prm}, authorization server metadata={As}",
            prmTemplate is not null ? "serving" : "absent",
            asTemplate is not null ? "serving" : "absent");

        return app;
    }

    /// <summary>
    /// Maps one document at its well-known path and any suffixed variant (RFC 8414/9728 path
    /// insertion), GET + OPTIONS, CORS-open. Suffixes all serve the same document: a client probing
    /// a path the document does not describe will reject the mismatch itself, which is the correct
    /// outcome and keeps Craft free of per-path knowledge.
    /// </summary>
    private static void MapDocument(WebApplication app, string path, string template)
    {
        IResult Handle(HttpContext context)
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            context.Response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "*";
            context.Response.Headers.AccessControlMaxAge = "86400";

            if (HttpMethods.IsOptions(context.Request.Method)) return Results.NoContent();

            // App Service terminates TLS upstream; the container sees http. The public origin is
            // always https, and Host survives the front end (custom domains included).
            return Results.Content(
                Render(template, $"https://{context.Request.Host.Value}"),
                "application/json");
        }

        app.MapMethods(path, ["GET", "OPTIONS"], Handle);
        app.MapMethods(path + "/{**suffix}", ["GET", "OPTIONS"], Handle);
    }

    /// <summary>
    /// The document template from the named app setting, or null (with the reason logged) when the
    /// setting is absent or its content is not valid JSON. Internal and env-injectable for tests.
    /// </summary>
    internal static string? ResolveTemplate(
        string settingName, Func<string, string?> getEnv, ILogger logger)
    {
        var raw = string.IsNullOrWhiteSpace(settingName) ? null : getEnv(settingName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Not an error: the hosted app writes the setting when the OAuth resource exists and
            // clears it when it goes away.
            logger.LogInformation("[Prm] App setting '{Setting}' is not set — document not served", settingName);
            return null;
        }

        // Validate once at startup so a malformed document is a log line, not a per-request 200
        // full of garbage that clients choke on. {origin} is a legal JSON string value, so the
        // template parses as-is.
        try
        {
            using var _ = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "[Prm] App setting '{Setting}' is not valid JSON — document not served", settingName);
            return null;
        }

        return raw;
    }

    /// <summary>Substitutes <see cref="OriginPlaceholder"/> with the request origin.</summary>
    internal static string Render(string template, string origin) =>
        template.Replace(OriginPlaceholder, origin, StringComparison.OrdinalIgnoreCase);
}
