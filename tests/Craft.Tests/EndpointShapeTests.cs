using System.Text.RegularExpressions;

namespace Craft.Tests;

/// <summary>
/// Guards one minimal-API footgun that produces a working build, a 200 response, and no output.
/// <para>
/// <c>MapGet</c> and friends are overloaded on both <c>RequestDelegate</c> and <c>Delegate</c>. An
/// expression-bodied async lambda that takes an <c>HttpContext</c> —
/// <c>async (HttpContext c) =&gt; Results.Json(x)</c> — is implicitly convertible to
/// <c>RequestDelegate</c>, because its body is a valid expression-statement and the value can simply
/// be discarded. Overload resolution therefore prefers the <c>RequestDelegate</c> overload, the
/// <c>IResult</c> is computed and thrown away, and the caller receives an empty 200 with no
/// Content-Type. Give the lambda a block body with an explicit <c>return</c> and it is no longer
/// convertible to <c>RequestDelegate</c>, so the correct overload binds.
/// </para>
/// <para>
/// This shipped on <c>/api/setup/status</c> and took the whole first-run wizard down with it: the
/// page's <c>await res.json()</c> threw on the empty body, and every control on it stayed disabled.
/// Nothing about that is visible in a build log, a type check, or a code review that is looking at
/// the body rather than the arrow — hence a test.
/// </para>
/// </summary>
public class EndpointShapeTests
{
    private static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, "EndpointSources");

    // `async (HttpContext ...)  =>  X` — capture the first character of the body.
    private static readonly Regex s_httpContextLambda =
        new(@"async\s*\(\s*HttpContext\b[^)]*\)\s*=>\s*(?<body>\S)", RegexOptions.Compiled);

    public static TheoryData<string> EndpointSources()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(SourceDirectory, "*.cs")) data.Add(Path.GetFileName(file));
        return data;
    }

    [Fact]
    public void EndpointSources_AreCopiedNextToTheTestBinary()
    {
        Assert.True(Directory.Exists(SourceDirectory),
            $"Endpoint sources were not copied to {SourceDirectory}. " +
            "Check the None/LinkBase item in Craft.Tests.csproj.");
        Assert.NotEmpty(Directory.GetFiles(SourceDirectory, "*.cs"));
    }

    /// <summary>
    /// Blanks whole-line comments, keeping the line count intact so reported line numbers still point
    /// at the real source. Needed because the rule is explained in prose right above the code it
    /// governs, and prose describing the broken shape is not the broken shape.
    /// </summary>
    private static string StripCommentLines(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith('*') ||
                trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                lines[i] = "";
            }
        }
        return string.Join('\n', lines);
    }

    [Theory]
    [MemberData(nameof(EndpointSources))]
    public void HttpContextLambdas_UseABlockBody(string fileName)
    {
        var source = StripCommentLines(File.ReadAllText(Path.Combine(SourceDirectory, fileName)));

        foreach (Match match in s_httpContextLambda.Matches(source))
        {
            var line = source.Take(match.Index).Count(c => c == '\n') + 1;

            Assert.True(match.Groups["body"].Value == "{",
                $"{fileName}:{line} — an async lambda taking HttpContext has an expression body. " +
                "It will bind the RequestDelegate overload and its return value will be silently " +
                "discarded, producing an empty 200. Rewrite it with a block body and an explicit " +
                "return:\n" +
                "    async (HttpContext context) =>\n" +
                "    {\n" +
                "        var result = await ...;\n" +
                "        return Results.Json(result);\n" +
                "    }");
        }
    }

    [Fact]
    public void TheStatusEndpointReturnsItsResult()
    {
        // The specific regression, named. /api/setup/status is the wizard's only input: an empty body
        // here disables every control on the page with no error anywhere.
        var source = File.ReadAllText(Path.Combine(SourceDirectory, "SetupEndpoints.cs"));

        var index = source.IndexOf("/api/setup/status", StringComparison.Ordinal);
        Assert.True(index >= 0, "the /api/setup/status route is gone");

        var handler = source[index..Math.Min(source.Length, index + 400)];
        Assert.Contains("return Results.Json", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardWouldHaveCaughtTheOriginalDefect()
    {
        // Proves the regex matches the shape that broke, so a passing suite means the rule is being
        // enforced rather than never firing.
        var broken = """
            app.MapGet("/api/setup/status", async (HttpContext context) =>
                Results.Json(await setupService.GetStatus(context.RequestAborted)));
            """;

        var match = s_httpContextLambda.Match(broken);
        Assert.True(match.Success);
        Assert.NotEqual("{", match.Groups["body"].Value);
    }
}
