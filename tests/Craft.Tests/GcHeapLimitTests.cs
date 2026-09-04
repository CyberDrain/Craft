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
    [InlineData(-1)]
    public void ProfileWithoutALimit_IsANoOp(int? mb)
    {
        // Omitted/null (or negative) = no opinion: keep the baseline, silently. A literal 0 is different
        // — see ProfileZero_DisablesTheCap.
        var profile = new SkuProfile { HttpPoolSize = 2, BgPoolSize = 2, GCHeapHardLimitMB = mb };
        var logs = new List<string>();

        var applied = GcHeapLimit.Apply(profile, logs.Add, _ => throw new InvalidOperationException("must not be called"));

        Assert.False(applied);
        Assert.Empty(logs);
    }

    [Fact]
    public void ProfileZero_DisablesTheCap()
    {
        // A profile's literal 0 disables the cap entirely — same meaning as CRAFT_GC_HEAP_LIMIT_MB=0 —
        // so a large tier uses container memory instead of the baked smallest-tier limit.
        var profile = new SkuProfile { HttpPoolSize = 2, BgPoolSize = 2, GCHeapHardLimitMB = 0 };
        var logs = new List<string>();
        ulong? requested = 123;   // sentinel: proves 0 is what reaches refresh

        var applied = GcHeapLimit.Apply(profile, logs.Add, bytes => requested = bytes);

        Assert.True(applied);
        Assert.Equal(0UL, requested);
        Assert.Contains(logs, l => l.Contains("disabled by SkuProfile", StringComparison.Ordinal));
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
        Assert.Contains(logs, l => l.Contains("GC heap hard limit change (1 MB) by SkuProfile failed", StringComparison.Ordinal));
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

    // ── CRAFT_GC_HEAP_LIMIT_MB: the per-instance escape hatch ─────────────────────────────────────────
    // Restores the control a fleet-wide profile would otherwise take away: the profile writes the limit
    // through AppContext, which overrides a hand-set DOTNET_GCHeapHardLimit, so an operator needs a way
    // back in without editing the shared profile list. readEnv is injected here to keep the process
    // environment untouched, exactly as refresh is injected to keep the real GC untouched.

    [Fact]
    public void EnvOverride_WinsOverProfile()
    {
        var profile = new SkuProfile { GCHeapHardLimitMB = 5120 };
        var logs = new List<string>();
        ulong? requested = null;

        var applied = GcHeapLimit.Apply(profile, logs.Add, bytes => requested = bytes, () => "8192");

        Assert.True(applied);
        Assert.Equal(8192UL * 1024 * 1024, requested);   // env value, not the profile's 5120
        Assert.Contains(logs, l => l.Contains("set to 8192 MB by CRAFT_GC_HEAP_LIMIT_MB", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvOverrideZero_DisablesTheCap_OverridingTheProfile()
    {
        var profile = new SkuProfile { GCHeapHardLimitMB = 5120 };
        var logs = new List<string>();
        ulong? requested = 123;   // sentinel: proves the 0 is what actually reaches refresh

        var applied = GcHeapLimit.Apply(profile, logs.Add, bytes => requested = bytes, () => "0");

        Assert.True(applied);
        Assert.Equal(0UL, requested);
        Assert.Contains(logs, l => l.Contains("disabled by CRAFT_GC_HEAP_LIMIT_MB", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvOverrideZero_DisablesTheCap_WithNoProfile()
    {
        var logs = new List<string>();
        ulong? requested = 123;

        var applied = GcHeapLimit.Apply(null, logs.Add, bytes => requested = bytes, () => "0");

        Assert.True(applied);
        Assert.Equal(0UL, requested);
        Assert.Contains(logs, l => l.Contains("disabled by CRAFT_GC_HEAP_LIMIT_MB", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("-5")]      // negative = invalid, not a request to disable; that's what 0 is for
    public void EnvOverrideAbsentOrInvalid_DefersToProfile(string? env)
    {
        var profile = new SkuProfile { GCHeapHardLimitMB = 5120 };
        var logs = new List<string>();
        ulong? requested = null;

        var applied = GcHeapLimit.Apply(profile, logs.Add, bytes => requested = bytes, () => env);

        Assert.True(applied);
        Assert.Equal(5120UL * 1024 * 1024, requested);
        Assert.Contains(logs, l => l.Contains("set to 5120 MB by SkuProfile", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvOverrideZero_WhenRefreshRefuses_LogsAndKeepsBaseline()
    {
        var logs = new List<string>();

        var applied = GcHeapLimit.Apply(null, logs.Add,
            _ => throw new InvalidOperationException("nope"), () => "0");

        Assert.False(applied);
        Assert.Contains(logs, l => l.Contains(
            "GC heap hard limit change (disable) by CRAFT_GC_HEAP_LIMIT_MB failed", StringComparison.Ordinal));
    }
}
