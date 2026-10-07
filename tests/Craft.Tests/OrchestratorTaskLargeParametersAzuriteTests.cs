using System.Text.Json;
using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// End-to-end guard against a real table backend: a scheduled task whose parameters embed a whole policy
/// template is far over Azure Table's 64 KiB-per-property limit, and so can be a run's PostExecution
/// parameters. The payload row must persist and rehydrate byte-for-byte, and the run must still move through
/// claim, finish and its barrier: those are real entity-group transactions here, which cannot split a row.
///
/// Azurite by default, a real account via CRAFT_TEST_TABLE_CONNECTION; skipped, not failed, when neither
/// is reachable.
/// </summary>
[Collection(LargeAllocationSerialTests.Name)]
public class OrchestratorTaskLargeParametersAzuriteTests
{
    private static async Task<WorkStore?> TryConnectAsync()
    {
        var settings = new CraftSettings();
        var connection = Environment.GetEnvironmentVariable("CRAFT_TEST_TABLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection))
            settings.Auth.UserStorageConnection = connection;
        else
            settings.Storage.AllowDevelopmentStorage = true;

        settings.Orchestrator.TablePrefix = "aztp" + Guid.NewGuid().ToString("N")[..8];

        var backing = new AzureTableStore(settings);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await backing.PingAsync(cts.Token);
        }
        catch
        {
            return null;
        }

        var store = new WorkStore(NullLogger<WorkStore>.Instance, settings, backing);
        await store.InitializeAsync();
        return store;
    }

    private static string AsString(object? v) => v switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? "",
        _ => v?.ToString() ?? ""
    };

    [Theory]
    [InlineData(80_000)]      // > 64 KiB per-property: column split
    [InlineData(1_200_000)]   // > 1 MiB entity: forces a cross-row split too
    public async Task ARunWithParametersOverTheLimit_PersistsRehydratesAndCompletes(int settingsChars)
    {
        var store = await TryConnectAsync();
        if (store == null) return;

        var big = new string('T', settingsChars);
        var started = DateTime.UtcNow;
        var header = new RunHeader
        {
            RunKey = WorkStore.RunKeyFor("UserTaskOrchestrator_contoso.com", started),
            Name = "UserTaskOrchestrator_contoso.com",
            Priority = 2,
            StartedUtc = started,
            TaskScriptName = "Invoke-CraftTask",
            PostExecFunctionName = "Agg",
            PostExecParametersJson = JsonSerializer.Serialize(new { blob = big }),
        };

        try
        {
            await store.CreateRunAsync(header,
                [new WorkStore.NewTask("task-0", new() { ["Tenant"] = "contoso.com", ["Settings"] = big })]);

            var claim = Assert.Single(await store.ClaimAsync(header.RunKey, 10, "w", TimeSpan.FromMinutes(5), true));
            var rehydrated = await store.GetPayloadAsync(header.RunKey, claim.Seq);
            Assert.Equal("contoso.com", AsString(rehydrated!["Tenant"]));
            Assert.Equal(big, AsString(rehydrated["Settings"]));

            var barrier = await store.FinishAsync(header.RunKey, [new WorkStore.Finish(claim.Seq, "Completed", Owner: "w")]);
            Assert.True(barrier!.ReachedBarrier);
            Assert.Equal(header.PostExecParametersJson, await store.GetPostExecParametersAsync(header.RunKey));

            var aggregate = Assert.Single(await store.ClaimAsync(header.RunKey, 10, "w", TimeSpan.FromMinutes(5), true));
            Assert.Equal(WorkStore.AggregateSeq, aggregate.Seq);
            var done = await store.FinishAsync(header.RunKey, [new WorkStore.Finish(aggregate.Seq, "Completed", Owner: "w")]);
            Assert.True(done!.Completed);
            Assert.Equal("Completed", done.Header.Status);
        }
        finally
        {
            await store.DeleteRunAsync(header.RunKey);
        }
    }
}
