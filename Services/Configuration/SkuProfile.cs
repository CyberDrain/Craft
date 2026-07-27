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
}
