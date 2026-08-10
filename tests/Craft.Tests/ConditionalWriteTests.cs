using Craft.Configuration;
using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// The claim primitive that lets storage, rather than a process's memory, be the source of truth for
/// queued work: a conditional replace guarded by the ETag each row was read with.
///
/// The guards matter as much as the write. A claim that silently degrades into an unconditional upsert
/// takes work another worker is already running, and a claim that partially applies marks rows as owned
/// by a worker that never receives them — both are worse than failing.
///
/// Round-trip behaviour against a live backend (412 → false, and specifically NOT falling back to
/// individual upserts the way <see cref="ICraftTableStore.UpsertBatchAsync"/> does) needs Azurite and is
/// not covered here; the claim/refill logic that consumes it is tested against a conditional-capable
/// fake instead.
/// </summary>
public class ConditionalWriteTests
{
    private static AzureTableStore NewStore()
    {
        // The connection string is resolved lazily on first real use and every guard below rejects
        // before the client is touched, so no backend or configuration is needed here.
        return new AzureTableStore(new CraftSettings());
    }

    private static StoreRow Row(string rowKey, string? etag)
        => new("run", rowKey) { ETag = etag, Properties = { ["Status"] = "Running" } };

    [Fact]
    public async Task EmptyBatchSucceedsWithoutTouchingTheBackend()
    {
        // No rows means nothing to guard and nothing to write. It must not be an error, and it must not
        // reach for a connection — refill runs this on every idle poll.
        Assert.True(await NewStore().TryReplaceBatchAsync("tasks", "run", []));
    }

    [Fact]
    public async Task RowWithoutAnETagIsRejected()
    {
        // A row with no ETag was never read from storage, so "replace whatever is there" is not a claim.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewStore().TryReplaceBatchAsync("tasks", "run", [Row("task-1", etag: null)]));

        Assert.Contains("ETag", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchLargerThanOneTransactionIsRejected()
    {
        // Beyond the transaction limit the backend would apply this in pieces. For a claim that means
        // rows marked owned by a worker that never gets them — refuse instead of half-claiming.
        var rows = Enumerable.Range(0, 101).Select(i => Row($"task-{i}", "W/\"x\"")).ToList();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewStore().TryReplaceBatchAsync("tasks", "run", rows));

        Assert.Contains("100", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ETagIsCarriedOnTheRowSoAReadCanGuardItsWriteBack()
    {
        // The whole mechanism rests on this surviving read → mutate → conditional write.
        var read = new StoreRow("run", "task-1") { ETag = "W/\"datetime'2026-08-10'\"" };
        read["Status"] = "Pending";

        read["Status"] = "Running";

        Assert.Equal("W/\"datetime'2026-08-10'\"", read.ETag);
        Assert.Equal("Running", read.GetString("Status"));
    }
}
