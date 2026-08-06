using Craft.Hosting.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The discovery documents are served verbatim from app settings, so what needs proving is the
/// gating (absent / malformed all mean "serve nothing", never "serve something wrong") and the
/// {origin} substitution — a wrong resource value makes spec-compliant MCP clients reject the
/// document outright.
/// </summary>
public class PrmEndpointTests
{
    private const string Document =
        """{"resource":"{origin}/api/ExecMcp","authorization_servers":["{origin}"],"scopes_supported":["https://host.example/user_impersonation"],"bearer_methods_supported":["header"]}""";

    private const string AsDocument =
        """{"issuer":"{origin}","authorization_endpoint":"https://login.microsoftonline.com/tid/oauth2/v2.0/authorize","registration_endpoint":"{origin}/api/PublicMcpRegister"}""";

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void SettingAbsent_ServesNothing()
    {
        Assert.Null(PrmEndpoint.ResolveTemplate("CRAFT_PRM", _ => null, NullLogger.Instance));
    }

    [Fact]
    public void SettingNameEmpty_ServesNothing()
    {
        Assert.Null(PrmEndpoint.ResolveTemplate("", Env(("CRAFT_PRM", Document)), NullLogger.Instance));
    }

    [Fact]
    public void ValidJson_ReturnsTheDocumentVerbatim()
    {
        var template = PrmEndpoint.ResolveTemplate(
            "CRAFT_PRM", Env(("CRAFT_PRM", Document)), NullLogger.Instance);

        Assert.Equal(Document, template);
    }

    [Fact]
    public void MalformedJson_ServesNothing()
    {
        var template = PrmEndpoint.ResolveTemplate(
            "CRAFT_PRM", Env(("CRAFT_PRM", """{"resource": no-quotes}""")), NullLogger.Instance);

        Assert.Null(template);
    }

    [Fact]
    public void AuthServerDocument_ResolvesIndependentlyOfThePrmDocument()
    {
        // Only the AS setting present — the PRM document is absent, the AS one still serves.
        var env = Env(("CRAFT_PRM_AS", AsDocument));

        Assert.Null(PrmEndpoint.ResolveTemplate("CRAFT_PRM", env, NullLogger.Instance));
        Assert.Equal(AsDocument, PrmEndpoint.ResolveTemplate("CRAFT_PRM_AS", env, NullLogger.Instance));
    }

    [Fact]
    public void Render_SubstitutesOriginEverywhere()
    {
        var rendered = PrmEndpoint.Render(AsDocument, "https://dev.cipp.app");

        Assert.Contains("\"issuer\":\"https://dev.cipp.app\"", rendered);
        Assert.Contains("\"registration_endpoint\":\"https://dev.cipp.app/api/PublicMcpRegister\"", rendered);
        Assert.DoesNotContain("{origin}", rendered);
    }

    [Fact]
    public void Render_WithoutPlaceholder_IsVerbatim()
    {
        const string fixedDoc = """{"resource":"https://fixed.example/api/mcp"}""";

        Assert.Equal(fixedDoc, PrmEndpoint.Render(fixedDoc, "https://ignored.example"));
    }
}
