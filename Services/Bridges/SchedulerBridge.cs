using Craft.Orchestration;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge so PowerShell can query/set the scheduler timezone without DI.
/// PS usage: [Craft.Services.SchedulerBridge]::SetTimezone("America/New_York")
///           [Craft.Services.SchedulerBridge]::GetTimezone()
/// </summary>
public static class SchedulerBridge
{
    private static SchedulerService? s_service;

    public static void Initialize(SchedulerService service) => s_service = service;

    /// <summary>
    /// Validate and apply a new timezone. Throws if the ID is invalid.
    /// All tasks with TZOffset=true will use the new timezone on the next evaluation cycle.
    /// </summary>
    public static void SetTimezone(string timezoneId) =>
        (s_service ?? throw new InvalidOperationException("SchedulerService not initialized"))
            .SetTimezone(timezoneId);

    /// <summary>Returns the current timezone ID, or empty string if UTC.</summary>
    public static string GetTimezone() => s_service?.GetTimezone() ?? "";
}
