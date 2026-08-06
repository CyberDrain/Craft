using Craft.Configuration;
using Craft.Hosting.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The PRM document is served verbatim from one app setting, so what needs proving is the gating
/// (disabled / absent / malformed all mean "serve nothing", never "serve something wrong") and the
/// {origin} substitution — a wrong resource value makes spec-compliant MCP clients reject the
/// document outright.
/// </summary>
public class PrmEndpointTests
{
    private const string Document =
        """{"resource":"{origin}/api/ExecMcp","authorization_servers":["https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0"],"scopes_supported":["https://host.example/user_impersonation"],"bearer_methods_supported":["header"]}""";

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void Disabled_ServesNothing_EvenWhenTheSettingIsPresent()
    {
        var template = PrmEndpoint.ResolveTemplate(
            new PrmSettings { Enabled = false },
            Env(("CRAFT_PRM", Document)),
            NullLogger.Instance);

        Assert.Null(template);
    }

    [Fact]
    public void Enabled_ButSettingAbsent_ServesNothing()
    {
        var template = PrmEndpoint.ResolveTemplate(
            new PrmSettings { Enabled = true }, _ => null, NullLogger.Instance);

        Assert.Null(template);
    }

    [Fact]
    public void Enabled_WithValidJson_ReturnsTheDocumentVerbatim()
    {
        var template = PrmEndpoint.ResolveTemplate(
            new PrmSettings { Enabled = true },
            Env(("CRAFT_PRM", Document)),
            NullLogger.Instance);

        Assert.Equal(Document, template);
    }

    [Fact]
    public void Enabled_WithMalformedJson_ServesNothing()
    {
        var template = PrmEndpoint.ResolveTemplate(
            new PrmSettings { Enabled = true },
            Env(("CRAFT_PRM", """{"resource": no-quotes}""")),
            NullLogger.Instance);

        Assert.Null(template);
    }

    [Fact]
    public void CustomSettingName_IsHonoured()
    {
        var template = PrmEndpoint.ResolveTemplate(
            new PrmSettings { Enabled = true, SettingName = "MY_PRM" },
            Env(("MY_PRM", Document)),
            NullLogger.Instance);

        Assert.Equal(Document, template);
    }

    [Fact]
    public void Render_SubstitutesOriginEverywhere()
    {
        var rendered = PrmEndpoint.Render(Document, "https://dev.cipp.app");

        Assert.Contains("\"resource\":\"https://dev.cipp.app/api/ExecMcp\"", rendered);
        Assert.DoesNotContain("{origin}", rendered);
    }

    [Fact]
    public void Render_WithoutPlaceholder_IsVerbatim()
    {
        const string fixedDoc = """{"resource":"https://fixed.example/api/mcp"}""";

        Assert.Equal(fixedDoc, PrmEndpoint.Render(fixedDoc, "https://ignored.example"));
    }
}
