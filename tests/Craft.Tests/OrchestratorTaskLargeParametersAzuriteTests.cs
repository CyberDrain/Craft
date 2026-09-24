using System.Text.Json;
using Craft.Configuration;
using Craft.Orchestration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// End-to-end guard for the failure this whole change exists for: a scheduled task whose Parameters
/// embed a whole policy template serialize to more than Azure Table's 64 KiB-per-property limit, so the
/// orchestrator's Tasks row used to 400 with PropertyValueTooLarge, get dropped, and the task could never
/// be rehydrated at dispatch ("Parameters could not be rehydrated at dispatch — the Tasks-table row is
/// missing"). With large-entity splitting in the backing store, the task row persists and both read paths
/// the orchestrator uses — GetRunAsync (partition scan) and GetTaskParametersAsync (point read, the
/// dispatch rehydrate) — return the Parameters byte-for-byte.
///
/// Azurite by default, a real account via CRAFT_TEST_TABLE_CONNECTION; skipped, not failed, when neither
/// is reachable.
/// </summary>
[Collection(LargeAllocationSerialTests.Name)]
public class OrchestratorTaskLargeParametersAzuriteTests
{
    private static async Task<OrchestratorTableStore?> TryConnectAsync()
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

        var store = new OrchestratorTableStore(NullLogger<OrchestratorTableStore>.Instance, settings, backing);
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
    [InlineData(80_000)]      // > 64 KiB per-property: the exact reported failure (column split)
    [InlineData(1_200_000)]   // > 1 MiB entity: forces a cross-row split too
    public async Task ATaskWhoseParametersExceedTheLimit_PersistsAndRehydrates(int settingsChars)
    {
        var store = await TryConnectAsync();
        if (store == null) return;

        const string run = "UserTaskOrchestrator_contoso.com";
        var big = new string('T', settingsChars);
        var parameters = new Dictionary<string, object>
        {
            ["Tenant"] = "contoso.com",
            ["Settings"] = big,
        };

        try
        {
            await store.UpsertRunAsync(new OrchestratorRun
            {
                Name = run,
                Status = "Running",
                Priority = 2,
                StartedUtc = DateTime.UtcNow,
                TaskScriptName = "ExecScheduledCommand",
                Tasks = [new OrchestratorTaskItem { Id = "task-0", Status = "Pending" }]
            });

            // The exact write path Start-UserTasksOrchestrator uses to enqueue a task's payload.
            await store.UpsertTaskBatchAsync(run, new List<OrchestratorTaskItem>
            {
                new() { Id = "task-0", Status = "Pending", Parameters = parameters }
            });

            // Dispatch rehydrate: a point read of the one task's Parameters.
            var rehydrated = await store.GetTaskParametersAsync(run, "task-0");
            Assert.NotNull(rehydrated);
            Assert.Equal("contoso.com", AsString(rehydrated!["Tenant"]));
            Assert.Equal(big, AsString(rehydrated["Settings"]));

            // Whole-run read: the partition scan reassembles the same task.
            var loaded = await store.GetRunAsync(run);
            Assert.NotNull(loaded);
            var task = Assert.Single(loaded!.Tasks);
            Assert.Equal(big, AsString(task.Parameters["Settings"]));
        }
        finally
        {
            await store.CleanupRunAsync(run);
        }
    }
}
