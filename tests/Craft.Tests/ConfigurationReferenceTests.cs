using System.Text.Json;
using Craft.Configuration;
using Microsoft.Extensions.Configuration;

namespace Craft.Tests;

/// <summary>
/// Keeps <c>appsettings.example.jsonc</c> honest.
/// <para>
/// That file documents every setting alongside its default, and it is the thing people copy from. The
/// defaults themselves live on the C# properties in <see cref="CraftSettings"/>. Nothing except this
/// test stops the two drifting: the example is never loaded, so a stale value in it produces no error
/// anywhere — it just quietly misinforms whoever reads it.
/// </para>
/// </summary>
public class ConfigurationReferenceTests
{
    private static string ExamplePath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.example.jsonc");

    private static IConfigurationRoot LoadExample()
    {
        // The ASP.NET Core JSON provider parses with JsonCommentHandling.Skip and AllowTrailingCommas,
        // which is exactly why the comments in the example file work at all. Using the real provider
        // here means this test also proves the file would still load if someone renamed it back
        // to appsettings.json.
        using var stream = File.OpenRead(ExamplePath);
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    [Fact]
    public void ExampleFile_Exists()
    {
        Assert.True(File.Exists(ExamplePath),
            $"appsettings.example.jsonc was not copied next to the test binary ({ExamplePath}). " +
            "Check the None/CopyToOutputDirectory item in Craft.Tests.csproj.");
    }

    [Fact]
    public void ExampleFile_IsParseableByTheAspNetCoreJsonProvider()
    {
        var config = LoadExample();
        Assert.Equal("Craft", config["App:Name"]);
    }

    [Fact]
    public void ExampleFile_IsNotStrictJson_AndSoMustNotBeNamedDotJson()
    {
        // Documents *why* the extension is .jsonc: System.Text.Json with default options — i.e. every
        // generic JSON tool, schema validator and linter — rejects it. If this ever starts passing, the
        // comments are gone and the file could go back to being a plain .json.
        var text = File.ReadAllText(ExamplePath);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(text));
    }

    /// <summary>
    /// Every value the example file actually sets, mapped to the corresponding C# default. The example
    /// exists to show defaults, so a mismatch means the example is lying to whoever copies from it.
    /// </summary>
    public static TheoryData<string, object> DocumentedDefaults()
    {
        var settings = new CraftSettings();
        return new TheoryData<string, object>
        {
            { "App:Name",                              settings.Name },
            { "App:Realtime:Enabled",                  settings.Realtime.Enabled },
            { "App:Worker:HttpPoolSize",               settings.Worker.HttpPoolSize },
            { "App:Worker:BgPoolSize",                 settings.Worker.BgPoolSize },
            { "App:Auth:CookieName",                   settings.Auth.CookieName },
            { "App:Auth:UserTableName",                settings.Auth.UserTableName },
            { "App:Scheduler:ConfigFile",              settings.Scheduler.ConfigFile },
            { "App:Scheduler:CheckIntervalSeconds",    settings.Scheduler.CheckIntervalSeconds },
            { "App:Scheduler:ApplyTZOffset",           settings.Scheduler.ApplyTZOffset },
            { "App:Orchestrator:TablePrefix",          settings.Orchestrator.TablePrefix },
            { "App:Orchestrator:MaxRetries",           settings.Orchestrator.MaxRetries },
            { "App:Cache:MaxEntries",                  settings.Cache.MaxEntries },
            { "App:Cache:DefaultTtlSeconds",           settings.Cache.DefaultTtlSeconds },
            { "App:Scripts:PermissionExtraction:Enabled", settings.Scripts.PermissionExtraction.Enabled },
        };
    }

    [Theory]
    [MemberData(nameof(DocumentedDefaults))]
    public void DocumentedValue_MatchesTheCSharpDefault(string key, object expected)
    {
        var documented = LoadExample()[key];

        Assert.True(documented is not null,
            $"'{key}' is asserted here but is no longer present in appsettings.example.jsonc.");

        var actual = Convert.ChangeType(documented, expected.GetType(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(expected.Equals(actual),
            $"appsettings.example.jsonc documents {key}={documented}, but the C# default is " +
            $"'{expected}'. The example is documentation for the defaults — update the example file " +
            "(or stop listing this key in it), because right now it misleads anyone copying from it.");
    }

    /// <summary>
    /// The example must never become load-bearing. Craft ships no appsettings.json, so a deployment
    /// that sets nothing gets the C# defaults — this asserts a couple of representative ones directly,
    /// independent of any file.
    /// </summary>
    [Fact]
    public void Defaults_AreUsableWithNoConfigurationFileAtAll()
    {
        var settings = new CraftSettings();

        Assert.Equal(2, settings.Worker.HttpPoolSize);
        Assert.Equal(4, settings.Worker.BgPoolSize);
        Assert.Equal("craft-session", settings.Auth.CookieName);
        Assert.False(settings.Realtime.Enabled);
        Assert.False(settings.Setup.Enabled);
        Assert.True(settings.Health.Enabled);
    }
}
