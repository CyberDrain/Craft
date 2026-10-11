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
