using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

public class ModuleFunctionIndexTests
{
    [Fact]
    public void ConcurrentFirstLookupsAllSeeTheFullIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "craft-modidx-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(root, "Modules", "Big");
        Directory.CreateDirectory(module);
        try
        {
            File.WriteAllLines(Path.Combine(module, "Big.psm1"),
                Enumerable.Range(0, 20_000).Select(i => $"function Get-Big{i} {{ {i} }}"));
            var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, new CraftSettings());
            repo.LoadAll(root);

            using var start = new Barrier(16);
            var results = new bool[16];
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                start.SignalAndWait();
                results[i] = repo.IsModuleFunction("Get-Big19999");
            });

            Assert.All(results, Assert.True);
            Assert.False(repo.IsModuleFunction("Get-Missing"));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class ModuleFunctionScanTests
{
    private static readonly System.Text.RegularExpressions.Regex Expected = (System.Text.RegularExpressions.Regex)typeof(ScriptRepository)
        .GetField("FunctionBlockRegex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

    private static List<string> Regex(string text) => Expected.Matches(text).Select(m => m.Groups[1].Value).ToList();
    private static List<string> Scan(string text) => ScriptRepository.ModuleFunctionNames(new StringReader(text)).ToList();

    [Theory]
    [InlineData("function Get-A {\n}\nfunction Get-B{ }\n")]
    [InlineData("function Get-A\n{\n}\r\nFUNCTION Get-B\r\n\r\n  {\r\n}")]
    [InlineData("function\nGet-A {")]
    [InlineData("  function Indented {\nfunctionX Y {\nfunction Get-C $x {\nfunction Get-D {")]
    [InlineData("function Get-A\nfunction Get-B {")]
    [InlineData("function\n\nfunction Get-B {")]
    [InlineData("function Get-A\r{\rfunction Get-B {")]
    [InlineData("function Get-Ä_1 {")]
    [InlineData("function")]
    [InlineData("")]
    public void FindsWhatTheRegexFinds(string text) => Assert.Equal(Regex(text), Scan(text));

    [Fact]
    public void MatchesTheRegexOnRandomText()
    {
        string[] parts = ["function", "Function", " ", "\t", "\n", "\r\n", "\r", "{", "}", "Get-X", "x", "-", "#", "$a", "  function A {"];
        var random = new Random(1234);
        for (var run = 0; run < 3000; run++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => parts[random.Next(parts.Length)]));
            Assert.True(Regex(text).SequenceEqual(Scan(text)), $"differs for: {text.Replace("\n", "\n").Replace("\r", "\r")}");
        }
    }
}
