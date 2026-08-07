using Craft.Hosting;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge exposing container startup metrics to PowerShell and HTTP endpoints.
/// Populated during pool initialization. Read-only after startup completes.
///
/// PS usage:
///   $info = [Craft.Services.StartupInfoBridge]::GetInfo()
///   $info.HttpReadyMs      # time in ms until first HTTP worker was ready
///   $info.IsFullyReady     # true once all pools are done
///   $info.Phase            # current phase: "Starting", "HttpReady", "Ready"
/// </summary>
/// <remarks>
/// Uninitialized policy: <see cref="GetInfo"/> throws (startup progress is always wired early).
/// </remarks>
public static class StartupInfoBridge
{
    private static StartupProgressService? s_progress;

    internal static void Initialize(StartupProgressService progress) => s_progress = progress;

    /// <summary>Get the current startup statistics snapshot.</summary>
    public static StartupStats GetInfo() =>
        s_progress?.Stats ?? throw new InvalidOperationException("StartupInfoBridge not initialized");
}
