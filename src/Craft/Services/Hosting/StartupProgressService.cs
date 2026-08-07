using Craft.Services;

namespace Craft.Hosting;

/// <summary>
/// Owns <see cref="StartupStats"/> for the process. Domain code (worker pool, Program)
/// records progress here; PowerShell reads via <c>StartupInfoBridge.GetInfo()</c>.
/// </summary>
public sealed class StartupProgressService
{
    private readonly StartupStats _stats = new();

    public StartupStats Stats => _stats;

    public void SetReadinessMode(string mode) => _stats.ReadinessMode = mode;
    public void SetWarmupMode(string mode) => _stats.WarmupMode = mode;
    public void SetCpuCount(int count) => _stats.CpuCount = count;

    public void SetPoolConfig(int httpSize, int bgSize)
    {
        _stats.HttpPoolSize = httpSize;
        _stats.BgPoolSize = bgSize;
    }

    public void SetModuleCounts(int shared, int httpOnly, int bgOnly)
    {
        _stats.SharedModuleCount = shared;
        _stats.HttpOnlyModuleCount = httpOnly;
        _stats.BgOnlyModuleCount = bgOnly;
    }

    public void SetBaseWorkerDone(long ms, int functionCount)
    {
        _stats.BaseWorkerMs = ms;
        _stats.BaseFunctionCount = functionCount;
        _stats.Phase = "BaseReady";
    }

    public void SetWarmupDone(long ms) => _stats.WarmupMs = ms;

    public void SetHttpReady(long ms, int functionCount)
    {
        _stats.HttpReadyMs = ms;
        _stats.HttpFunctionCount = functionCount;
        _stats.Phase = "HttpReady";
    }

    public void SetHttpPoolFull(long ms) => _stats.HttpPoolFullMs = ms;

    public void SetBgReady(long ms, int functionCount)
    {
        _stats.BgReadyMs = ms;
        _stats.BgFunctionCount = functionCount;
    }

    public void SetFullyReady(long ms)
    {
        _stats.FullyReadyMs = ms;
        _stats.Phase = "Ready";
        _stats.IsFullyReady = true;
    }
}
