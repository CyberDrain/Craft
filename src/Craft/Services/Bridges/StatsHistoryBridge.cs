using System.Text.Json;
using Craft.Hosting;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge exposing stats history to PowerShell and HTTP endpoints.
///
/// PS usage:
///   $history = [Craft.Services.StatsHistoryBridge]::GetHistory(60)   # last 60 minutes
///   $history = [Craft.Services.StatsHistoryBridge]::GetHistory(1440)  # last 24 hours
///   $allJson = [Craft.Services.StatsHistoryBridge]::GetHistoryJson(10080, 500)  # 7 days, max 500 points
///   $count   = [Craft.Services.StatsHistoryBridge]::GetCount()
/// </summary>
/// <remarks>
/// Uninitialized policy: reads soft no-op (empty list / 0) when Background roles are off.
/// </remarks>
public static class StatsHistoryBridge
{
    private static StatsHistoryService? s_service;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static void Initialize(StatsHistoryService service) => s_service = service;

    /// <summary>Get history for the last N minutes, optionally downsampled.</summary>
    public static List<StatsDataPoint> GetHistory(int lastMinutes = 60, int? maxPoints = null)
    {
        if (s_service == null) return new();
        var since = DateTime.UtcNow.AddMinutes(-lastMinutes);
        return s_service.GetHistory(since: since, maxPoints: maxPoints);
    }

    /// <summary>Get history as a JSON string (for PowerShell convenience).</summary>
    public static string GetHistoryJson(int lastMinutes = 60, int? maxPoints = null)
    {
        var data = GetHistory(lastMinutes, maxPoints);
        return JsonSerializer.Serialize(data, s_jsonOptions);
    }

    /// <summary>Get the total number of data points stored.</summary>
    public static int GetCount() => s_service?.GetCount() ?? 0;

    /// <summary>Get history between two UTC timestamps.</summary>
    public static List<StatsDataPoint> GetHistoryRange(DateTime since, DateTime until, int? maxPoints = null)
    {
        if (s_service == null) return new();
        return s_service.GetHistory(since: since, until: until, maxPoints: maxPoints);
    }
}
