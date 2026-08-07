using Craft.Configuration;
using Craft.PowerShellHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Startup script indexing — <see cref="ScriptRepository.LoadAll"/> must discover routes from
/// multi-file module trees and survive parallel parse of many .ps1 files.
/// </summary>
public class ScriptRepositoryLoadTests
{
    [Fact]
    public void LoadAll_IndexesFunctionsAndRoutes_FromParallelPs1Parse()
    {
        var root = Path.Combine(Path.GetTempPath(), "craft-scriptrepo-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(root, "Modules", "TestHttp");
        Directory.CreateDirectory(module);

        try
        {
            // Mix: bare using line (must be stripped), function defs, bare script.
            File.WriteAllText(Path.Combine(module, "Invoke-Alpha.ps1"),
                "using namespace System.Net\nfunction Invoke-Alpha { param($Request) 'a' }\n");
            File.WriteAllText(Path.Combine(module, "Invoke-Beta.ps1"),
                "function Invoke-Beta { param($Request) 'b' }\n");
            File.WriteAllText(Path.Combine(module, "gamma.ps1"),
                "# bare timer-style script\n'g'\n");

            // Enough files to exercise Parallel.ForEach meaningfully.
            for (var i = 0; i < 32; i++)
            {
                File.WriteAllText(Path.Combine(module, $"Invoke-Item{i:D2}.ps1"),
                    $"function Invoke-Item{i:D2} {{ param($Request) '{i}' }}\n");
            }

            var settings = new CraftSettings();
            settings.Scripts.HttpModules = ["TestHttp"];
            settings.Scripts.BackgroundScriptDirs = [];

            var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
            repo.LoadAll(root);

            Assert.True(repo.HttpRoutes.ContainsKey("Alpha"));
            Assert.True(repo.HttpRoutes.ContainsKey("Beta"));
            Assert.Equal("Invoke-Alpha", repo.HttpRoutes["Alpha"]);
            Assert.NotNull(repo.GetByRoute("Alpha"));
            Assert.NotNull(repo.GetByName("Invoke-Beta"));
            Assert.NotNull(repo.GetByName("gamma"));

            for (var i = 0; i < 32; i++)
                Assert.True(repo.HttpRoutes.ContainsKey($"Item{i:D2}"), $"missing route Item{i:D2}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void LoadAll_StripsUsingDirectives_WithoutLosingFunctionBody()
    {
        var root = Path.Combine(Path.GetTempPath(), "craft-scriptrepo-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(root, "Modules", "TestHttp");
        Directory.CreateDirectory(module);

        try
        {
            File.WriteAllText(Path.Combine(module, "Invoke-WithUsing.ps1"),
                "using module Foo\r\nusing assembly Bar\r\nfunction Invoke-WithUsing { 'ok' }\r\n");

            var settings = new CraftSettings();
            settings.Scripts.HttpModules = ["TestHttp"];
            settings.Scripts.BackgroundScriptDirs = [];

            var repo = new ScriptRepository(NullLogger<ScriptRepository>.Instance, settings);
            repo.LoadAll(root);

            var entry = repo.GetByRoute("WithUsing");
            Assert.NotNull(entry);
            Assert.Equal("Invoke-WithUsing", entry!.FunctionName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
