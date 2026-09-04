namespace Craft.Configuration;

/// <summary>
/// Maps a host environment (an env var value and/or CPU count) to a worker pool sizing.
/// Used by the SkuProfiles list on WorkerSettings.
///
/// Matching:
///   - SkuEnv: name of the env var to read (e.g. "WEBSITE_SKU" on Azure App Service).
///             Null or empty = SKU criterion is wildcard (match any host).
///   - Sku:    expected value of that env var, compared case-insensitively
///             (e.g. "Basic", "PremiumV3"). Ignored when SkuEnv is empty.
///   - Cpu:    compared to Environment.ProcessorCount. Null or 0 = match any count.
///
/// All specified criteria must match. First matching entry in the list wins.
/// Letting the profile name the env var means downstream apps can target any host
/// (Azure, AWS, GCP, k8s, plain Docker) without backend changes — just point at
/// whatever env var the operator uses to identify the host tier.
/// </summary>
public class SkuProfile
{
    /// <summary>Name of the env var to read for the SKU identifier (e.g. "WEBSITE_SKU"). Null/empty = wildcard.</summary>
    public string? SkuEnv { get; set; }

    /// <summary>Expected value of the env var named by SkuEnv (e.g. "Basic"). Compared case-insensitively.</summary>
    public string? Sku { get; set; }

    /// <summary>CPU count to match (Environment.ProcessorCount). Null or 0 = match any count.</summary>
    public int? Cpu { get; set; }

    /// <summary>HTTP worker pool size to apply when this profile matches.</summary>
    public int HttpPoolSize { get; set; }

    /// <summary>Background worker pool size to apply when this profile matches.</summary>
    public int BgPoolSize { get; set; }

    /// <summary>
    /// Optional GC heap hard limit, in MB, to apply when this profile matches. Three-way, mirroring the
    /// <c>CRAFT_GC_HEAP_LIMIT_MB</c> override:
    /// <list type="bullet">
    /// <item><description>Omitted / null (or negative) = no opinion, keep the process baseline (typically
    /// the DOTNET_GCHeapHardLimit env var baked into the image for the smallest tier).</description></item>
    /// <item><description><c>0</c> = disable the cap entirely, so the GC uses the container's own memory
    /// allowance — for tiers with more memory than the baked limit lets them use.</description></item>
    /// <item><description>&gt; 0 = set that many MB.</description></item>
    /// </list>
    /// The baked env var is consumed by the CLR before any managed code runs, so this is applied after
    /// the fact via <see cref="Craft.Hosting.GcHeapLimit"/> — raising or removing the limit is always
    /// safe; a positive value the heap has already outgrown is refused and logged.
    /// <para>
    /// Per-instance override: the <c>CRAFT_GC_HEAP_LIMIT_MB</c> env var wins over this value (a positive
    /// value sets the cap; <c>0</c> disables the cap entirely), so an operator can hand-tune one host
    /// without editing the fleet-wide profile list.
    /// </para>
    /// </summary>
    public int? GCHeapHardLimitMB { get; set; }
}
