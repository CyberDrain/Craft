using Craft.Realtime;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static publish surface for the realtime channel, so downstream PowerShell (and Craft's own C#) can
/// push job events without an HTTP round-trip — mirrors <see cref="AppLifecycleBridge"/> /
/// <see cref="SchedulerBridge"/>. Delivery, gating, GUID/size enforcement and storage live in
/// <see cref="RealtimeService"/>.
///
/// PS usage:
///   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "start",  @{ done = 0; total = 300 })
///   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "update", @{ done = 142; total = 300 })
///   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "end",    @{ done = 300; total = 300 })
///
/// Only <c>userId</c> and <c>jobId</c> (a GUID) are required; everything else is optional. Every call is
/// best-effort and never throws back to the caller.
/// </summary>
public static class RealtimeBridge
{
    private static RealtimeService? s_service;

    public static void Initialize(RealtimeService service) => s_service = service;

    /// <summary>Signal-only (mode defaults to "update", no payload).</summary>
    public static void Publish(string userId, string jobId) =>
        Publish(userId, jobId, null, null, null, null, null, null);

    public static void Publish(string userId, string jobId, string mode) =>
        Publish(userId, jobId, mode, null, null, null, null, null);

    public static void Publish(string userId, string jobId, string mode, object? data) =>
        Publish(userId, jobId, mode, data, null, null, null, null);

    /// <summary>Publish with a click-to-navigate url object.</summary>
    public static void Publish(string userId, string jobId, string mode, object? data, string? urlHref, string? urlLabel) =>
        Publish(userId, jobId, mode, data, urlHref, urlLabel, null, null);

    /// <summary>Full form. <paramref name="mode"/> is start | update | end (default update).</summary>
    public static void Publish(string userId, string jobId, string? mode, object? data,
        string? urlHref, string? urlLabel, int? status, string? message)
    {
        try
        {
            s_service?.Publish(userId, jobId, mode, data, urlHref, urlLabel, status, message);
        }
        catch
        {
            // Realtime is best-effort and must never disrupt the caller's work.
        }
    }
}
