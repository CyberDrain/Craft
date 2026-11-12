using Craft.Configuration;
using Craft.Orchestration;
using Craft.PowerShellHost;
using Craft.Services;
using Microsoft.Extensions.Configuration;
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

/// <summary>
/// Host-initiated work (timers, planner runs, queue tasks) resolves its command here and runs it on a background
/// worker: any module function resolves, preloaded or loaded on demand, but never an HTTP entrypoint.
/// </summary>
public sealed class BackgroundScriptResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "craft-bgres-" + Guid.NewGuid().ToString("N"));

    public BackgroundScriptResolutionTests()
    {
        Write("Modules/BgMod/BgMod.psm1", "function Start-Nightly { }\nfunction Get-Bg { }");
        Write("Modules/Other/Other.psm1", "function Get-Other { }");
        Write("Modules/HttpMod/Public/Invoke-ListThing.ps1", "function Invoke-ListThing {\n    param($Request, $TriggerMetadata)\n}");
        Write("timers.json", """[ { "Id": "nightly", "Command": "Start-Nightly", "Cron": "0 0 1 * * *" } ]""");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private (PowerShellRunnerService Runner, CraftSettings Settings) NewRunner(params string[] bgModules)
    {
        var settings = new CraftSettings();
        settings.Scripts.HttpModules = ["HttpMod"];
        settings.Worker.BgModules = [.. bgModules];
        var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
        repo.LoadAll(_root);
        return (new PowerShellRunnerService(NullLogger<PowerShellRunnerService>.Instance, null!, repo, settings), settings);
    }

    [Fact]
    public void ModulesOutsideTheBackgroundPreloadStillResolve()
    {
        var (runner, _) = NewRunner("BgMod");

        Assert.Equal("Get-Bg", runner.FindScript("Get-Bg"));
        Assert.Equal("Get-Other", runner.FindScript("Get-Other"));
    }

    [Fact]
    public void HttpEntrypointsNeverResolve()
    {
        var (runner, _) = NewRunner();

        Assert.Null(runner.FindScript("ListThing"));
        Assert.Null(runner.FindScript("Invoke-ListThing"));
    }

    [Fact]
    public void RunNowFiresOnlyConfiguredTimers()
    {
        var (runner, settings) = NewRunner("BgMod");
        settings.Scheduler.ConfigFile = Path.Combine(_root, "timers.json");
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var pool = new PowerShellWorkerPool(new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings),
            NullLogger<PowerShellWorkerPool>.Instance, config, settings);
        var limiter = new BackgroundTaskLimiter(NullLogger<BackgroundTaskLimiter>.Instance, config, settings, pool);
        using var jobs = new JobManager(NullLogger<JobManager>.Instance, settings, limiter);
        var scheduler = new SchedulerService(NullLogger<SchedulerService>.Instance, runner, limiter, null!, jobs,
            settings, pool, null!, Craft.Endpoints.NativeScheduledTasks.Empty, null!);
        scheduler.Start(DateTimeOffset.UtcNow);

        Assert.False(scheduler.RunNow("Get-Bg"));
        Assert.Equal(0, jobs.QueuedCount);
        Assert.True(scheduler.RunNow("start-nightly"));
        Assert.Equal(1, jobs.QueuedCount);
    }
}
