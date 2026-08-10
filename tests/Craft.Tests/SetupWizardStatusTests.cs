using System.Runtime.CompilerServices;
using Craft.Configuration;
using Craft.Setup;
using Craft.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The first-run wizard has exactly one input: the <c>usersStatus</c> block of
/// <c>/api/setup/status</c>. Whether the operator can add a superadmin, and whether the
/// authentication step is reachable at all, is decided from it — so every field it can report has to
/// mean what the page assumes it means. The page-side branch table is tested in tests/setup-wizard/.
/// </summary>
public class SetupWizardStatusTests
{
    private static SetupService NewService(ICraftTableStore store, CraftSettings? settings = null) =>
        new(NullLogger<SetupService>.Instance, settings ?? new CraftSettings(), store);

    // ── The probe ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReachableAndEmpty_IsTheFirstRunState()
    {
        var status = await NewService(new FakeStore()).CheckAllowedUsersStatus(CancellationToken.None);

        Assert.True(status.Connected);
        Assert.False(status.HasUsers);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task AnExistingUser_IsReported()
    {
        var store = new FakeStore();
        store.Rows.Add(new StoreRow("User", "admin@contoso.com"));

        var status = await NewService(store).CheckAllowedUsersStatus(CancellationToken.None);

        Assert.True(status.Connected);
        Assert.True(status.HasUsers);
    }

    [Fact]
    public async Task UnderscorePrefixedRows_DoNotCountAsUsers()
    {
        // Underscore keys are the table's own metadata. Counting them as users would tell the wizard
        // step 1 was already done on a table that has no superadmin in it — the operator would sail
        // past the only step that keeps them out of a locked-out app.
        var store = new FakeStore();
        store.Rows.Add(new StoreRow("User", "_metadata"));
        store.Rows.Add(new StoreRow("User", "_schema"));

        var status = await NewService(store).CheckAllowedUsersStatus(CancellationToken.None);

        Assert.True(status.Connected);
        Assert.False(status.HasUsers);
    }

    [Fact]
    public async Task AStoreThatThrows_IsReportedRatherThanPropagated()
    {
        // The probe must always produce an answer: an exception here would 500 /api/setup/status, and
        // the page has no way to tell that apart from the network being down.
        var store = new FakeStore { EnsureTableError = new InvalidOperationException("no route to host") };

        var status = await NewService(store).CheckAllowedUsersStatus(CancellationToken.None);

        Assert.False(status.Connected);
        Assert.False(status.HasUsers);
        Assert.Equal("no route to host", status.Error);
    }

    [Fact]
    public async Task AQueryThatThrowsMidStream_IsAlsoReported()
    {
        // Table creation can succeed and the query still fail (throttling, expired SAS).
        var store = new FakeStore { QueryError = new InvalidOperationException("server busy") };

        var status = await NewService(store).CheckAllowedUsersStatus(CancellationToken.None);

        Assert.False(status.Connected);
        Assert.Equal("server busy", status.Error);
    }

    [Fact]
    public async Task TheProbeCreatesTheTableItIsAbout()
    {
        // On a fresh storage account the table does not exist yet, and "missing" must read as
        // "empty", not as "unreachable".
        var store = new FakeStore();

        var status = await NewService(store).CheckAllowedUsersStatus(CancellationToken.None);

        Assert.Contains("allowedUsers", store.EnsuredTables);
        Assert.True(status.Connected);
    }

    [Fact]
    public async Task TheProbeAndTheSeedAgreeOnTheTableName()
    {
        // Two independent sanitizers. If they ever diverge the wizard reads one table and writes
        // another, and the status poll never observes the user it just created.
        var settings = new CraftSettings();
        settings.Auth.UserTableName = "my-allowed-users!";
        var store = new FakeStore();
        var service = NewService(store, settings);

        await service.CheckAllowedUsersStatus(CancellationToken.None);
        await service.SeedFirstUser("admin@contoso.com", CancellationToken.None);

        Assert.Equal(2, store.EnsuredTables.Count);
        Assert.Single(store.EnsuredTables.Distinct());
        Assert.Equal(store.EnsuredTables[0], store.UpsertedTo.Single());
    }

    // ── The server-side invariant the page now fails open against ───────────────────────────────
    // The wizard deliberately leaves Add Superadmin clickable when it cannot read status, because
    // these guards — not a greyed button — are what actually protect the table.

    [Fact]
    public async Task SeedingRefusesATableThatAlreadyHasUsers()
    {
        var store = new FakeStore();
        store.Rows.Add(new StoreRow("User", "existing@contoso.com"));
        var service = NewService(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SeedFirstUser("admin@contoso.com", CancellationToken.None));

        Assert.Contains("already contains users", ex.Message, StringComparison.Ordinal);
        // Not just "the new UPN is absent" — no write was attempted at all. An endpoint that returned
        // 400 and still upserted would satisfy the weaker assertion.
        Assert.Empty(store.UpsertedTo);
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task TheGuardScansPastMetadataRowsToFindAUser()
    {
        // The scan must not stop at the first underscore row. If it did, a table whose metadata sorts
        // ahead of its users would read as empty and the guard would wave a second superadmin through.
        var store = new FakeStore();
        store.Rows.Add(new StoreRow("User", "_metadata"));
        store.Rows.Add(new StoreRow("User", "_schema"));
        store.Rows.Add(new StoreRow("User", "existing@contoso.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(store).SeedFirstUser("admin@contoso.com", CancellationToken.None));

        Assert.Empty(store.UpsertedTo);
    }

    [Fact]
    public async Task ReseedingTheSameUserIsRefusedRatherThanOverwritingTheirRoles()
    {
        // UpsertAsync replaces. Without the guard, re-running setup against an established table would
        // silently reset a real user's roles to the seed defaults — a privilege change disguised as a
        // no-op. Case is varied because the seed path lowercases and the row key is already lowercase.
        var store = new FakeStore();
        store.Rows.Add(new StoreRow("User", "existing@contoso.com")
        {
            Properties = { ["Roles"] = "[\"readonly\"]" }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(store).SeedFirstUser("EXISTING@contoso.com", CancellationToken.None));

        Assert.Empty(store.UpsertedTo);
        Assert.Equal("[\"readonly\"]", store.Rows.Single().Properties["Roles"]);
    }

    [Fact]
    public async Task SeedingIsStillAllowedOnAMetadataOnlyTable()
    {
        var store = new FakeStore();
        store.Rows.Add(new StoreRow("User", "_metadata"));

        await NewService(store).SeedFirstUser("admin@contoso.com", CancellationToken.None);

        Assert.Contains(store.Rows, r => r.RowKey == "admin@contoso.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SeedingRejectsABlankUpn(string upn)
    {
        // The page validates too, but the endpoint is reachable directly and is the real gate.
        await Assert.ThrowsAsync<ArgumentException>(
            () => NewService(new FakeStore()).SeedFirstUser(upn, CancellationToken.None));
    }

    [Fact]
    public async Task SeededUpnIsNormalisedSoTheNextStatusReadMatchesIt()
    {
        var store = new FakeStore();

        await NewService(store).SeedFirstUser("  Admin@Contoso.COM  ", CancellationToken.None);

        Assert.Equal("admin@contoso.com", store.Rows.Single().RowKey);
    }

    [Fact]
    public async Task SeededUserGetsTheSuperadminRole()
    {
        var store = new FakeStore();

        await NewService(store).SeedFirstUser("admin@contoso.com", CancellationToken.None);

        var row = store.Rows.Single();
        Assert.Equal("[\"superadmin\"]", row.Properties["Roles"]);
        Assert.Equal("[\"superadmin\"]", row.Properties["ManualRoles"]);
        Assert.Equal("Manual", row.Properties["Source"]);
    }

    [Fact]
    public async Task SeedingThenProbing_ReportsTheUserSoStepTwoUnlocks()
    {
        // The round trip the wizard's poll depends on: after a successful seed the very next status
        // read has to say hasUsers, or the authentication step stays greyed out forever.
        var store = new FakeStore();
        var service = NewService(store);

        await service.SeedFirstUser("admin@contoso.com", CancellationToken.None);
        var status = await service.CheckAllowedUsersStatus(CancellationToken.None);

        Assert.True(status.HasUsers);
    }

    // ── Fake ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="ICraftTableStore"/>. Only the four members the setup path touches do
    /// anything; the rest throw so an accidental new dependency shows up as a failing test rather
    /// than a silent no-op.
    /// </summary>
    private sealed class FakeStore : ICraftTableStore
    {

        // Claims are not exercised by this fake. Fail loudly rather than pretend the guard held —
        // a silent 'true' here would look exactly like a successful claim.
        public Task<bool> TryReplaceBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows,
            CancellationToken ct = default) => throw new NotSupportedException();
        public List<StoreRow> Rows { get; } = [];
        public List<string> EnsuredTables { get; } = [];
        public List<string> UpsertedTo { get; } = [];
        public Exception? EnsureTableError { get; init; }
        public Exception? QueryError { get; init; }

        public Task EnsureTableAsync(string table, CancellationToken ct = default)
        {
            if (EnsureTableError != null) return Task.FromException(EnsureTableError);
            EnsuredTables.Add(table);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<StoreRow> QueryTableAsync(
            string table, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (QueryError != null) throw QueryError;
            foreach (var row in Rows)
            {
                yield return row;
                await Task.Yield();
            }
        }

        public Task UpsertAsync(string table, StoreRow row, CancellationToken ct = default)
        {
            UpsertedTo.Add(table);
            Rows.Add(row);
            return Task.CompletedTask;
        }

        public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertBatchAsync(string table, string partitionKey, IReadOnlyList<StoreRow> rows, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<StoreRow?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<StoreRow> QueryPartitionAsync(string table, string partitionKey, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeletePartitionAsync(string table, string partitionKey, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
