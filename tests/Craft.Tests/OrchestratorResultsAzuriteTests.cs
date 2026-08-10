using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Verifies the results path against a real Azure Tables implementation (Azurite), because the
/// properties that matter here are the BACKEND's, not ours:
///
///   1. The chunking bounds are real. Azure Tables caps a property at 64 KiB and an entity at 1 MiB;
///      the unit-test fake is a Dictionary and will happily accept a 1.2M-character string in one
///      property. So MaxPropertyChars/MaxEntityChars — and the multi-row spill they force — are only
///      genuinely exercised here. A fake cannot fail the way the thing that ships fails.
///   2. Rows come back in server-side (PartitionKey, RowKey) order, not insertion order, so a spilled
///      result's parts arrive interleaved with other results' rows. Reassembly has to complete by
///      chunk count rather than by arrival order against a backend that actually reorders.
///
/// What is being pinned on top of that: one result is one line in the JSON Lines handoff file,
/// whatever its storage shape. That is the invariant Invoke-CraftPostExecution relies on to read the
/// aggregate with File.ReadLines instead of materialising 50-150MB of it as a string.
///
/// Runs against the local emulator by default, or a real storage account when
/// CRAFT_TEST_TABLE_CONNECTION is set. Skipped, not failed, when neither is reachable, so this is safe
/// in CI — which does mean a skip looks like a pass; see TryConnectAsync for how to re-prove it.
/// </summary>
public class OrchestratorResultsAzuriteTests
{
    private static async Task<OrchestratorTableStore?> TryConnectAsync()
    {
        var settings = new CraftSettings();

        var connection = Environment.GetEnvironmentVariable("CRAFT_TEST_TABLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection))
            settings.Auth.UserStorageConnection = connection;
        else
            settings.Storage.AllowDevelopmentStorage = true;

        // Unique per run so repeated runs cannot see each other's rows, and so a real account is never
        // left holding fixtures under a name a later run would reuse.
        settings.Orchestrator.TablePrefix = "azrt" + Guid.NewGuid().ToString("N")[..8];

        var backing = new AzureTableStore(settings);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await backing.PingAsync(cts.Token);
        }
        catch
        {
            // Nothing to verify against. NOTE: an early return is indistinguishable from a pass — to
            // re-prove these actually ran, swap the call sites' null check for Assert.NotNull(store)
            // and confirm they still pass.
            return null;
        }

        var store = new OrchestratorTableStore(
            NullLogger<OrchestratorTableStore>.Instance, settings, backing);
        await store.InitializeAsync();
        return store;
    }

    /// <summary>A result whose JSON exceeds the per-property limit and so gets chunked.</summary>
    private static string BigJson(int chars) => "\"" + new string('x', chars - 2) + "\"";

    [Fact]
    public async Task EveryStorageShape_RoundTrips_AsExactlyOneLine()
    {
        var store = await TryConnectAsync();
        if (store == null) return;

        // The three shapes StoreResultAsync can produce against the real limits: one property,
        // many properties in one row, and a spill across rows.
        var small = """{"kind":"small"}""";
        var chunked = BigJson(90_000);        // >30k chars → many properties, still one row
        var spilled = BigJson(1_200_000);     // >450k chars → spills across rows

        await store.StoreResultAsync("run", "a-small", small);
        await store.StoreResultAsync("run", "b-chunked", chunked);
        await store.StoreResultAsync("run", "c-spilled", spilled);

        var path = Path.Combine(Path.GetTempPath(), $"craft-azurite-{Guid.NewGuid():N}.jsonl");
        try
        {
            var count = await store.StreamResultsToJsonLinesAsync("run", path);

            // One line per result — a 1.2M-char result spilled over several rows must not become
            // several lines, or the reader would see one result as many.
            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            Assert.Equal(3, count);
            Assert.Equal(3, lines.Count);

            // And reassembly is byte-exact, not merely the right shape.
            Assert.Contains(small, lines);
            Assert.Contains(chunked, lines);
            Assert.Contains(spilled, lines);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            await store.CleanupRunAsync("run");
        }
    }

    [Fact]
    public async Task ManyResults_StreamOneLineEach_WithSpillsInterleaved()
    {
        var store = await TryConnectAsync();
        if (store == null) return;

        // Spilled results sit between ordinary ones, so the backend's (PartitionKey, RowKey) ordering
        // interleaves each spill's parts with other results' rows. Anything that assumed a result's
        // rows arrive together breaks here and cannot break against the fake.
        var expected = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var json = i % 5 == 0 ? BigJson(600_000) : $$"""{"tenant":"t{{i}}","i":{{i}}}""";
            expected.Add(json);
            await store.StoreResultAsync("run", $"task{i:D2}", json);
        }

        var path = Path.Combine(Path.GetTempPath(), $"craft-azurite-{Guid.NewGuid():N}.jsonl");
        try
        {
            var count = await store.StreamResultsToJsonLinesAsync("run", path);
            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            Assert.Equal(20, count);
            Assert.Equal(20, lines.Count);
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal),
                lines.OrderBy(x => x, StringComparer.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            await store.CleanupRunAsync("run");
        }
    }
}
