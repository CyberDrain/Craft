using Azure.Data.Tables;
using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// Pure round-trip tests for <see cref="EntitySplitter"/> — no backend. These prove the split/reassemble
/// invariant that <see cref="AzureTableStore"/> relies on: whatever goes in comes back byte-identical,
/// however the storage limits forced it to be broken up, and a partial set of rows is refused rather than
/// silently truncated. Uses real large strings so it never mutates the splitter's global tuning knobs.
/// </summary>
[Collection(LargeAllocationSerialTests.Name)]
public class EntitySplitterTests
{
    private static string Text(int chars, char c = 'x') => new(c, chars);

    private static TableEntity Entity(string pk, string rk, params (string Key, object Value)[] props)
    {
        var e = new TableEntity(pk, rk);
        foreach (var (k, v) in props) e[k] = v;
        return e;
    }

    private static TableEntity RoundTrip(TableEntity entity)
    {
        var split = EntitySplitter.Split(entity);
        // Reassemble sees exactly the physical rows a query would return.
        var back = EntitySplitter.Reassemble(split.Rows).ToList();
        Assert.Single(back);
        return back[0];
    }

    [Fact]
    public void SmallEntity_IsNotEngaged_AndPassesThroughUnchanged()
    {
        var entity = Entity("p", "r", ("Value", "hello"), ("N", 7));
        var split = EntitySplitter.Split(entity);

        Assert.False(split.Engaged);
        Assert.Single(split.Rows);
        Assert.Same(entity, split.Rows[0]);
    }

    [Fact]
    public void OversizedProperty_SplitsAcrossColumns_InOneRow_AndRejoins()
    {
        var value = Text(80_000); // > 32K limit, well under the 1 MiB entity cap
        var entity = Entity("p", "r", ("Status", "Pending"), ("ParametersJson", value));

        var split = EntitySplitter.Split(entity);

        Assert.True(split.Engaged);
        Assert.Single(split.Rows);                                   // column split stays one physical row
        Assert.False(split.Rows[0].ContainsKey("ParametersJson"));  // original replaced by chunks
        Assert.True(split.Rows[0].ContainsKey("ParametersJson_Part0"));
        Assert.True(split.Rows[0].ContainsKey(EntitySplitter.SplitOverPropsKey));

        var back = EntitySplitter.Reassemble(split.Rows).Single();
        Assert.Equal(value, back.GetString("ParametersJson"));
        Assert.Equal("Pending", back.GetString("Status"));
        Assert.False(back.ContainsKey(EntitySplitter.SplitOverPropsKey));
    }

    [Fact]
    public void HugeProperty_SplitsAcrossRows_AndRejoins()
    {
        var value = Text(1_200_000); // forces the entity over the 1 MiB cap → cross-row
        var entity = Entity("p", "r", ("Status", "Pending"), ("ParametersJson", value));

        var split = EntitySplitter.Split(entity);

        Assert.True(split.Engaged);
        Assert.True(split.Rows.Count > 1);                         // spilled onto extra rows
        Assert.Equal("r", split.Rows[0].RowKey);                   // root keeps the original RowKey
        Assert.All(split.Rows, r => Assert.Equal("r", r.GetString(EntitySplitter.OriginalEntityIdKey)));

        var back = EntitySplitter.Reassemble(split.Rows).Single();
        Assert.Equal("r", back.RowKey);
        Assert.Equal(value, back.GetString("ParametersJson"));
        Assert.Equal("Pending", back.GetString("Status"));
    }

    [Fact]
    public void SurrogatePairs_AreNotSplitDownTheMiddle()
    {
        // A string of astral-plane code points (each a surrogate pair) sized past the chunk boundary:
        // a naive cut would produce an invalid half-surrogate and corrupt the round-trip.
        var value = string.Concat(Enumerable.Repeat("\U0001F600", 40_000)); // 80k UTF-16 units
        var entity = Entity("p", "r", ("Data", value));

        var back = RoundTrip(entity);

        Assert.Equal(value, back.GetString("Data"));
    }

    [Fact]
    public void PlainRow_SupersedesLeftoverPartRows_OfTheSameIdentity()
    {
        // The entity was large (parts written), then rewritten small (a plain root at the same RowKey).
        // Reassembly must return the small value and ignore the stale parts.
        var big = EntitySplitter.Split(Entity("p", "r", ("Data", Text(1_200_000)))).Rows;
        var plain = Entity("p", "r", ("Data", "small-now"));

        var rows = new List<TableEntity> { plain };
        rows.AddRange(big.Where(r => r.RowKey != "r")); // leftover "-part{n}" rows only

        var back = EntitySplitter.Reassemble(rows).Single();
        Assert.Equal("small-now", back.GetString("Data"));
    }

    [Fact]
    public void MissingPartRow_IsReportedIncomplete_AndDropped_WithoutAffectingOthers()
    {
        var incomplete = EntitySplitter.Split(Entity("p", "gone", ("Data", Text(1_200_000)))).Rows;
        var whole = Entity("p", "here", ("Data", "fine"));

        // Drop the last part row of the "gone" entity to simulate a query that missed it.
        var rows = new List<TableEntity> { whole };
        rows.AddRange(incomplete.Take(incomplete.Count - 1));

        IncompleteEntityException? reported = null;
        var back = EntitySplitter.Reassemble(rows, onIncomplete: ex => reported = ex).ToList();

        Assert.NotNull(reported);
        Assert.Equal("gone", reported!.EntityRowKey);
        Assert.DoesNotContain(back, e => e.RowKey == "gone");
        Assert.Contains(back, e => e.RowKey == "here" && e.GetString("Data") == "fine");
    }

    [Fact]
    public void FindIncompleteGroups_IdentifiesEntitiesMissingRows()
    {
        var parts = EntitySplitter.Split(Entity("p", "r", ("Data", Text(1_200_000)))).Rows;
        var missingLast = parts.Take(parts.Count - 1).ToList();

        var incomplete = EntitySplitter.FindIncompleteGroups(missingLast);

        Assert.Contains(("p", "r"), incomplete);
    }
}
