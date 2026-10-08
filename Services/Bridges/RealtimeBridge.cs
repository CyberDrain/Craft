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
/// <see cref="SchedulerBridge"/>. Delivery, gating, job-id/size enforcement and storage live in
/// <see cref="RealtimeService"/>.
///
/// PS usage:
///   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "start",  @{ done = 0; total = 300 })
///   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "update", @{ done = 142; total = 300 })
///   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "end",    @{ done = 300; total = 300 })
///
/// Only <c>userId</c> and <c>jobId</c> (a short token: 1-128 letters, digits, <c>-_.:</c>) are required; everything
/// else is optional. Every call is best-effort and never throws back to the caller.
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

    /// <summary>
    /// Grant <paramref name="userId"/> the events of <paramref name="jobId"/>, so later
    /// <see cref="Notify(string)"/> calls reach them without knowing who they are. Call it from the request
    /// that started the job, with that request's principal name.
    /// PS usage: [Craft.Services.RealtimeBridge]::Watch($Request.Headers.'x-ms-client-principal-name', $JobId)
    /// </summary>
    public static void Watch(string userId, string jobId)
    {
        try { s_service?.Watch(userId, jobId, trackRun: false); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// <see cref="Watch"/>, and have Craft push the orchestrator run status for <paramref name="jobId"/>
    /// (a queue id; matched like <see cref="QueueStatusBridge.GetRun"/>) until the run finishes.
    /// </summary>
    public static void WatchRun(string userId, string jobId)
    {
        try { s_service?.Watch(userId, jobId, trackRun: true); }
        catch { /* best-effort */ }
    }

    /// <summary>Signal every user granted <paramref name="jobId"/> (mode update, no payload).</summary>
    public static void Notify(string jobId) => Notify(jobId, null, null);

    public static void Notify(string jobId, string? mode) => Notify(jobId, mode, null);

    /// <summary>Publish to every user granted <paramref name="jobId"/> through <see cref="Watch"/> or <see cref="WatchRun"/>.</summary>
    public static void Notify(string jobId, string? mode, object? data)
    {
        try { s_service?.Notify(jobId, mode, data); }
        catch { /* best-effort */ }
    }

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
