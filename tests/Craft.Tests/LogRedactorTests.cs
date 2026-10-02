using Craft.Configuration;
using Craft.Hosting;
using Craft.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Craft.Tests;

/// <summary>
/// Log lines must hide UPNs and customer domains while staying greppable and reversible: the same value
/// always masks to the same token, and LogBridge reveals it with the instance key.
/// </summary>
[Collection(nameof(LogRedactorTests))]
public class LogRedactorTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    public LogRedactorTests()
    {
        LogRedactor.Configure(enabled: true, allowDomains: ["cipp.app"]);
        LogRedactor.SetKey(Key);
    }

    [Theory]
    [InlineData("Processing jane.doe@contoso.com now")]
    [InlineData("tenant contoso.onmicrosoft.com failed")]
    [InlineData("Started: MailboxRules_contoso.onmicrosoft.com#GetMailbox")]
    [InlineData("GET https://graph.microsoft.com/v1.0/users/jane.doe%40contoso.com/mailFolders")]
    [InlineData("$filter=domain eq %27contoso.co.uk%27")]
    [InlineData("site contoso-my.sharepoint.com and autodiscover.contoso.com")]
    [InlineData("guest john_fabrikam.com#EXT#@contoso.onmicrosoft.com signed in")]
    [InlineData("first user Jane.Doe@Contoso.com")]
    public void MaskedLines_RevealToTheOriginal_CaseFoldedInTheHiddenPart(string line)
    {
        var masked = LogRedactor.Redact(line);

        Assert.NotEqual(line, masked);
        Assert.DoesNotContain("contoso", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(line, LogRedactor.Reveal(masked), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mask_KeepsPrefixAndSuffixReadable()
    {
        var masked = LogRedactor.Redact("jane.doe@contoso.onmicrosoft.com");

        Assert.Matches(@"^ja~[A-Za-z0-9_-]{11}~e@co~[A-Za-z0-9_-]{10}~o\.onmicrosoft\.com$", masked);
    }

    [Fact]
    public void SameDomain_MasksToTheSameToken_Everywhere()
    {
        var domain = LogRedactor.Redact("contoso.com");

        Assert.Contains(domain, LogRedactor.Redact("jane@contoso.com"), StringComparison.Ordinal);
        Assert.Contains(domain, LogRedactor.Redact("x %27contoso.com%27 y"), StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Redact("contoso.onmicrosoft.com")[..^".onmicrosoft.com".Length],
            LogRedactor.Redact("MailboxRules_contoso.onmicrosoft.com"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users?$top=999")]
    [InlineData("https://cippstg.table.core.windows.net/CippLogs()")]
    [InlineData("https://outlook.office365.com/adminapi/beta/x/InvokeCommand")]
    [InlineData("https://cipp.app/docs")]
    [InlineData("needs Tenant.Reports.Read and CIPP.Dashboard.Read")]
    [InlineData("System.Net.Http.HttpRequestException in Microsoft.Web")]
    [InlineData("rotated craft.1.log, loaded organizationmanagementroles.json, ran Push-Thing.ps1")]
    [InlineData("tenant 3f9a1c2b-7d4e-4f00-9a1b-2c3d4e5f6a7b v1.0.13 10.0.0.1")]
    public void InfrastructureAndNonDomains_AreLeftAlone(string line)
    {
        Assert.Equal(line, LogRedactor.Redact(line));
    }

    [Fact]
    public void Redact_IsIdempotent()
    {
        var once = LogRedactor.Redact("jane.doe@contoso.com in contoso.onmicrosoft.com");

        Assert.Equal(once, LogRedactor.Redact(once));
    }

    [Fact]
    public void AllowlistedEmailDomain_StillMasksTheUser()
    {
        Assert.Matches(@"^a~[A-Za-z0-9_-]{8}~n@microsoft\.com$", LogRedactor.Redact("admin@microsoft.com"));
    }

    [Fact]
    public void Reveal_WithAnotherKey_LeavesTokensInPlace()
    {
        var masked = LogRedactor.Redact("jane.doe@contoso.com");
        LogRedactor.SetKey(new byte[16]);

        Assert.Equal(masked, LogRedactor.Reveal(masked));
    }

    [Fact]
    public void Disabled_PassesLinesThrough()
    {
        LogRedactor.Configure(enabled: false, allowDomains: null);

        Assert.Equal("jane@contoso.com", LogRedactor.Redact("jane@contoso.com"));
    }

    [Fact]
    public void FileLog_IsMaskedOnDisk_AndRevealedThroughLogBridge()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craft-redact-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FileLoggerProvider(new FileLoggingSettings { Directory = dir });
            provider.CreateLogger("Test").LogWarning("PS warning: mailbox jane.doe@contoso.com in contoso.onmicrosoft.com");
            provider.Dispose();

            var raw = File.ReadAllText(Path.Combine(dir, "craft.log"));
            Assert.DoesNotContain("contoso", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ja~", raw, StringComparison.Ordinal);

            LogBridge.Initialize(provider);
            var line = Assert.Single(LogBridge.ReadLog(0, null, "jane.doe@contoso.com"), l => l.Length > 0);
            Assert.Contains("mailbox jane.doe@contoso.com in contoso.onmicrosoft.com", line, StringComparison.Ordinal);
            Assert.Single(LogBridge.ReadLog(0, null, null, null, null, null, null, @"contoso\.onmicrosoft"), l => l.Length > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConsoleSink_IsMasked()
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["App:FileLogging:Directory"] =
                Path.Combine(Path.GetTempPath(), "craft-redact-" + Guid.NewGuid().ToString("N"));
            builder.AddCraftLogging();
            using (var services = builder.Services.BuildServiceProvider())
            {
                services.GetRequiredService<ILoggerFactory>().CreateLogger("Test")
                    .LogError(new InvalidOperationException("denied for jane.doe@contoso.com"), "auth failed for jane.doe@contoso.com");
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        var text = output.ToString();
        Assert.Contains("auth failed for ja~", text, StringComparison.Ordinal);
        Assert.Contains("denied for ja~", text, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Key_IsCreatedOnce_AndReusedOnTheNextStart()
    {
        var store = new FakeTableStore();
        await LogRedactor.LoadKeyAsync(store);
        var first = LogRedactor.Redact("jane@contoso.com");

        LogRedactor.SetKey(new byte[16]);
        await LogRedactor.LoadKeyAsync(store);

        Assert.Equal(first, LogRedactor.Redact("jane@contoso.com"));
        Assert.Equal(1, store.Count("CraftInstanceKeys"));
    }
}
