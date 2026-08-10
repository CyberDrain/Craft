using Craft.Configuration;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Verifies the two properties that are the BACKEND's to provide, not ours, against a real Azure Tables
/// implementation (Azurite). The unit tests use a fake, and a fake is only ever as honest as whoever
/// wrote it — during development that fake got both of these wrong and the tests still passed.
///
///   1. Ordering is server-side and priority-first. Rows come back sorted by partition key then row key,
///      so "P00" precedes "P04" and, within a bucket, older precedes newer. Nothing sorts client-side.
///   2. Exclusivity is real optimistic concurrency. Two claimers racing the same rows cannot both win,
///      because the second one's If-Match no longer matches.
///
/// On leases: there is no Azure lease API here. Azure Tables does not have one — that is Blob. "Lease"
/// is only the name of an ordinary DateTimeOffset column, and the exclusivity comes entirely from ETag
/// conditional writes.
///
/// Runs against the local emulator by default, or a real storage account when
/// CRAFT_TEST_TABLE_CONNECTION is set. Skipped, not failed, when neither is reachable, so this is safe
/// in CI — which does mean a skip looks like a pass; see TryConnectAsync for how to re-prove it.
/// </summary>
public class JobQueueAzuriteTests
{
    private static async Task<JobQueueStore?> TryConnectAsync()
    {
        var settings = new CraftSettings();

        // Point at a real storage account by setting CRAFT_TEST_TABLE_CONNECTION; otherwise this runs
        // against the local emulator. Same assertions either way — the properties under test are the
        // backend's, and Azurite is a reimplementation of them, not the thing that ships.
        var connection = Environment.GetEnvironmentVariable("CRAFT_TEST_TABLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection))
            settings.Auth.UserStorageConnection = connection;
        else
            settings.Storage.AllowDevelopmentStorage = true;

        // Unique per run so repeated runs cannot see each other's rows, and so a real account is never
        // left holding fixtures under a name a later run would reuse.
        settings.Orchestrator.TablePrefix = "azqt" + Guid.NewGuid().ToString("N")[..8];

        var store = new AzureTableStore(settings);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await store.PingAsync(cts.Token);
        }
        catch
        {
            // Nothing to verify against. NOTE: an early return is indistinguishable from a pass — to
            // re-prove these actually ran, swap the call sites' null check for Assert.NotNull(queue)
            // and confirm they still pass.
            return null;
        }

        var queue = new JobQueueStore(NullLogger<JobQueueStore>.Instance, settings, store);
        await queue.InitializeAsync();
        return queue;
    }

    private static DateTime At(int minute) => new(2026, 8, 9, 2, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task BackendReturnsWorkPriorityFirstThenOldestFirst()
    {
        var queue = await TryConnectAsync();
        if (queue == null) return;

        // Inserted in the least helpful order: the bulk low-priority work first, urgent last, and the
        // newest P4 before the oldest. If any of this came back in insertion order the scheme is broken.
        await queue.EnqueueBatchAsync("StandardsApply",
            [("std-newer", 4), ("std-older", 4)], At(30));
        await queue.EnqueueAsync("StandardsApply", "std-oldest", 4, At(1));
        await queue.EnqueueAsync("DbCache", "cache-0", 6, At(0));
        await queue.EnqueueAsync("AuditLogIngest", "audit-0", 0, At(59));

        // Claim one at a time so the order the backend hands work out is directly observable.
        var order = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var claimed = await queue.ClaimBatchAsync($"w{i}", 1, TimeSpan.FromMinutes(20));
            order.Add(Assert.Single(claimed).TaskId);
        }

        // P0 first despite being queued last; P6 last despite being queued early.
        Assert.Equal("audit-0", order[0]);
        Assert.Equal("cache-0", order[4]);

        // Within P4, oldest first — std-oldest was queued at minute 1, the other two at minute 30.
        Assert.Equal("std-oldest", order[1]);
        Assert.Contains("std-newer", order.GetRange(2, 2));
        Assert.Contains("std-older", order.GetRange(2, 2));
    }

    [Fact]
    public async Task TwoClaimersRacingTheSameRowsCannotBothWin()
    {
        var queue = await TryConnectAsync();
        if (queue == null) return;

        await queue.EnqueueBatchAsync("run",
            Enumerable.Range(0, 6).Select(i => ($"task-{i:D2}", 4)).ToList(), At(0));

        // Both read the same rows before either writes, so both hold the same ETags.
        var a = queue.ClaimBatchAsync("worker-a", 6, TimeSpan.FromMinutes(20));
        var b = queue.ClaimBatchAsync("worker-b", 6, TimeSpan.FromMinutes(20));
        var results = await Task.WhenAll(a, b);

        var winners = results.Where(r => r.Count > 0).ToList();
        Assert.Single(winners);
        Assert.Equal(6, winners[0].Count);

        // And the rows really are owned now, not merely reported as claimed.
        Assert.Empty(await queue.ClaimBatchAsync("worker-c", 6, TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimableAndALiveOneIsNot()
    {
        var queue = await TryConnectAsync();
        if (queue == null) return;

        await queue.EnqueueAsync("run", "task-0", 4, At(0));

        var held = await queue.ClaimBatchAsync("worker-a", 1, TimeSpan.FromSeconds(2));
        Assert.Single(held);

        // Live lease: nobody else may take it.
        Assert.Empty(await queue.ClaimBatchAsync("worker-b", 1, TimeSpan.FromMinutes(20)));

        // Renewal extends it against the real clock.
        Assert.True(await queue.RenewAsync(held, "worker-a", TimeSpan.FromSeconds(3)));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Empty(await queue.ClaimBatchAsync("worker-b", 1, TimeSpan.FromMinutes(20)));

        // Lapsed: reclaimable, with no intervention from the holder. This is the crash-recovery path.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var reclaimed = await queue.ClaimBatchAsync("worker-b", 1, TimeSpan.FromMinutes(20));
        Assert.Equal("task-0", Assert.Single(reclaimed).TaskId);
    }
}
