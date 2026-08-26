using Craft.Configuration;
using Craft.Hosting;

namespace Craft.Tests;

/// <summary>
/// The GC heap hard limit rides on the same SkuProfile match as pool sizing, but unlike pool sizes
/// it cannot be set through configuration the runtime reads at startup — it has to land through
/// AppContext + <see cref="GC.RefreshMemoryLimit"/> after the CLR is already up. Getting this wrong
/// either leaves a large tier capped at the smallest tier's heap, or (worse) turns a sizing hint
/// into a startup failure.
/// </summary>
public class GcHeapLimitTests
{
    [Fact]
    public void NoMatchedProfile_IsANoOp()
    {
        var logs = new List<string>();

        var applied = GcHeapLimit.Apply(null, logs.Add, _ => throw new InvalidOperationException("must not be called"));

        Assert.False(applied);
        Assert.Empty(logs);   // like unused SkuProfiles: don't log on every start
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ProfileWithoutALimit_IsANoOp(int? mb)
    {
        var profile = new SkuProfile { HttpPoolSize = 2, BgPoolSize = 2, GCHeapHardLimitMB = mb };
        var logs = new List<string>();

        var applied = GcHeapLimit.Apply(profile, logs.Add, _ => throw new InvalidOperationException("must not be called"));

        Assert.False(applied);
        Assert.Empty(logs);
    }

    [Fact]
    public void ConfiguredLimit_IsAppliedInBytes_AndLogged()
    {
        var profile = new SkuProfile { GCHeapHardLimitMB = 5120 };
        var logs = new List<string>();
        ulong? requested = null;

        var applied = GcHeapLimit.Apply(profile, logs.Add, bytes => requested = bytes);

        Assert.True(applied);
        Assert.Equal(5120UL * 1024 * 1024, requested);
        Assert.Contains(logs, l => l.Contains("GC heap hard limit set to 5120 MB", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshRefusingTheLimit_LogsAndKeepsTheBaseline()
    {
        // GC.RefreshMemoryLimit throws when the new limit is below what the heap has already
        // committed. That must stay a logged hint, never a startup failure.
        var profile = new SkuProfile { GCHeapHardLimitMB = 1 };
        var logs = new List<string>();

        var applied = GcHeapLimit.Apply(profile, logs.Add,
            _ => throw new InvalidOperationException("RefreshMemoryLimit failed"));

        Assert.False(applied);
        Assert.Contains(logs, l => l.Contains("GC heap hard limit refresh to 1 MB failed", StringComparison.Ordinal));
    }

    [Fact]
    public void RealRefresh_ChangesTheProcessHeapLimit()
    {
        // Proves the actual mechanism (AppContext.SetData + GC.RefreshMemoryLimit) works, not just
        // our plumbing around it. Target slightly below the current budget: far above anything the
        // test host has committed, so it cannot destabilize parallel tests, while still being an
        // observable change. Restored afterwards (raising back is always allowed).
        var before = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (before < 2L * 1024 * 1024 * 1024) return;   // tiny CI container — not worth the risk

        var targetMB = (int)(before / (1024 * 1024)) - 128;
        try
        {
            var applied = GcHeapLimit.Apply(new SkuProfile { GCHeapHardLimitMB = targetMB }, _ => { });

            Assert.True(applied);
            Assert.Equal((long)targetMB * 1024 * 1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        }
        finally
        {
            AppContext.SetData("GCHeapHardLimit", (ulong)before);
            GC.RefreshMemoryLimit();
        }
    }
}
