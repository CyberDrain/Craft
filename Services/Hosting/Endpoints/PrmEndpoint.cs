using System.Text.Json;
using Craft.Configuration;

namespace Craft.Hosting.Endpoints;

/// <summary>
/// Serves OAuth 2.0 Protected Resource Metadata (RFC 9728) at
/// <c>/.well-known/oauth-protected-resource</c> (and any path-suffixed variant), which is how MCP
/// clients discover the authorization server and scopes for the hosted API.
///
/// <para>
/// The entire document comes verbatim from one app setting — Craft adds nothing and interprets
/// nothing beyond the per-request <c>{origin}</c> substitution. Metadata only: no authorize, token
/// or registration endpoints live here; the document points clients at Entra and EasyAuth remains
/// the sole token validator. See <see cref="PrmSettings"/> for why this exists instead of the
/// platform's own PRM preview.
/// </para>
/// </summary>
public static class PrmEndpoint
{
    /// <summary>Replaced per-request with <c>https://{host}</c> wherever it appears in the JSON.</summary>
    public const string OriginPlaceholder = "{origin}";

    /// <summary>
    /// Maps the well-known routes when PRM is enabled and the configured app setting holds valid
    /// JSON. The template is read once at startup — app setting changes restart the container.
    /// </summary>
    public static WebApplication MapCraftPrmEndpoint(
        this WebApplication app, CraftSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var template = ResolveTemplate(settings.Prm, Environment.GetEnvironmentVariable, logger);
        if (template is null) return app;

        IResult Serve(HttpRequest request)
        {
            // App Service terminates TLS upstream; the container sees http. The public origin is
            // always https, and Host survives the front end (custom domains included).
            return Results.Content(
                Render(template, $"https://{request.Host.Value}"),
                "application/json");
        }

        // CORS: the document is public by design and some clients fetch it from browser contexts.
        void Cors(HttpResponse response)
        {
            response.Headers.AccessControlAllowOrigin = "*";
            response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
        }

        app.MapMethods(PrmSettings.WellKnownPath, ["GET", "OPTIONS"], (HttpContext context) =>
        {
            Cors(context.Response);
            return HttpMethods.IsOptions(context.Request.Method)
                ? Results.NoContent()
                : Serve(context.Request);
        });

        // Suffixed variants (RFC 9728 path insertion) serve the same document. A client probing a
        // path the document's resource does not name will reject the mismatch itself — which is the
        // correct outcome, and keeps Craft free of any per-path knowledge.
        app.MapMethods(PrmSettings.WellKnownPath + "/{**suffix}", ["GET", "OPTIONS"],
            (HttpContext context, string suffix) =>
        {
            Cors(context.Response);
            return HttpMethods.IsOptions(context.Request.Method)
                ? Results.NoContent()
                : Serve(context.Request);
        });

        logger.LogInformation(
            "[Prm] Protected resource metadata: serving document from app setting '{Setting}' at {Path}",
            settings.Prm.SettingName, PrmSettings.WellKnownPath);

        return app;
    }

    /// <summary>
    /// The document template from the configured app setting, or null (with the reason logged) when
    /// PRM is disabled, the setting is absent, or its content is not valid JSON. Internal and
    /// env-injectable for tests.
    /// </summary>
    internal static string? ResolveTemplate(
        PrmSettings prm, Func<string, string?> getEnv, ILogger logger)
    {
        if (!prm.Enabled)
        {
            logger.LogInformation("[Prm] Protected resource metadata: disabled");
            return null;
        }

        var raw = string.IsNullOrWhiteSpace(prm.SettingName) ? null : getEnv(prm.SettingName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Enabled in config but this instance has no OAuth resource provisioned — the hosted
            // app writes the document when one exists and clears it when it goes away.
            logger.LogInformation(
                "[Prm] Protected resource metadata: enabled but app setting '{Setting}' is not set — not serving",
                prm.SettingName);
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
                "[Prm] App setting '{Setting}' is not valid JSON — protected resource metadata not served",
                prm.SettingName);
            return null;
        }

        return raw;
    }

    /// <summary>Substitutes <see cref="OriginPlaceholder"/> with the request origin.</summary>
    internal static string Render(string template, string origin) =>
        template.Replace(OriginPlaceholder, origin, StringComparison.OrdinalIgnoreCase);
}
