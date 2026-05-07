using System.Text.Json;

namespace Craft.Services;

/// <summary>
/// Static bridge allowing PowerShell (Get-CIPPQueueData) to query orchestrator/job
/// progress without HTTP round-trips. Returns data in the shape the CIPP frontend expects.
/// PS usage: [Craft.Services.QueueStatusBridge]::GetRunStatus($Reference, $QueueId)
/// </summary>
public static class QueueStatusBridge
{
    private static JobManager? s_jobManager;
    private static OrchestratorService? s_orchestratorService;

    public static void Initialize(JobManager jobManager, OrchestratorService? orchestratorService = null)
    {
        s_jobManager = jobManager;
        s_orchestratorService = orchestratorService;
    }

    /// <summary>
    /// Get queue/run status in the format expected by the CIPP frontend.
    /// Looks up by run name (Reference) or returns all recent runs.
    /// Returns a JSON string matching the Get-CIPPQueueData output shape.
    /// </summary>
    /// <param name="reference">Optional run reference/name to filter by (maps to RunName in JobManager)</param>
    /// <param name="queueId">Optional queue ID (same as reference in Craft context)</param>
    /// <returns>JSON array of queue status objects</returns>
    public static string GetRunStatus(string? reference = null, string? queueId = null)
    {
        if (s_jobManager == null) return "[]";

        // PowerShell converts $null to "" when calling .NET string parameters,
        // so treat empty strings the same as null.
        var effectiveQueueId = string.IsNullOrEmpty(queueId) ? null : queueId;
        var effectiveReference = string.IsNullOrEmpty(reference) ? null : reference;
        var lookup = effectiveQueueId ?? effectiveReference;
        var summaries = s_jobManager.GetRunSummaries();

        if (!string.IsNullOrEmpty(lookup))
        {
            // Try exact match on run name first
            var matched = summaries
                .Where(s => s.Name.Equals(lookup, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If no exact match, try matching by Reference via orchestrator service
            if (matched.Count == 0 && s_orchestratorService != null)
            {
                var runName = s_orchestratorService.FindRunByReference(lookup);
                if (runName != null)
                {
                    matched = summaries
                        .Where(s => s.Name.Equals(runName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }

            // Last resort: match run names ending with the lookup (QueueId is often the GUID suffix)
            if (matched.Count == 0)
            {
                matched = summaries
                    .Where(s => s.Name.EndsWith(lookup, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            summaries = matched;
        }
        else
        {
            // Return only runs from last 3 hours (matches legacy behavior)
            var cutoff = DateTime.UtcNow.AddHours(-3);
            summaries = summaries
                .Where(s => s.StartedUtc == null || s.StartedUtc > cutoff)
                .ToList();
        }

        var result = summaries.Select(s =>
        {
            var completedTasks = s.Completed + s.Failed;
            var total = Math.Max(s.Total, 1);
            var status = DeriveStatus(s);

            return new QueueStatusEntry
            {
                PartitionKey = "CippQueue",
                RowKey = s.Name,
                Name = s.Name,
                Link = "",
                Reference = s_orchestratorService?.GetRunReference(s.Name) ?? s.Name,
                TotalTasks = s.Total,
                CompletedTasks = completedTasks,
                RunningTasks = s.Running,
                FailedTasks = s.Failed,
                PercentComplete = Math.Round(((double)completedTasks / total) * 100, 1),
                PercentFailed = Math.Round(((double)s.Failed / total) * 100, 1),
                PercentRunning = Math.Round(((double)s.Running / total) * 100, 1),
                Tasks = GetTaskDetails(s.Name),
                Status = status,
                Timestamp = s.StartedUtc?.ToString("O") ?? DateTime.UtcNow.ToString("O")
            };
        }).ToList();

        return JsonSerializer.Serialize(result, s_jsonOptions);
    }

    private static string DeriveStatus(JobRunSummary s)
    {
        if (s.Queued == 0 && s.Running == 0)
        {
            return s.Failed > 0 ? "Completed (with errors)" : "Completed";
        }
        if (s.Running > 0 || s.Completed > 0 || s.Failed > 0)
        {
            return "Running";
        }
        return "Queued";
    }

    private static List<TaskDetail> GetTaskDetails(string runName)
    {
        if (s_jobManager == null) return [];

        var jobs = s_jobManager.GetJobs(runName, limit: 100);
        return jobs.Select(j => new TaskDetail
        {
            Timestamp = (j.CompletedUtc ?? j.StartedUtc ?? j.QueuedUtc).ToString("O"),
            Name = ExtractTaskDisplayName(j.Name, runName),
            Status = j.Status
        }).ToList();
    }

    /// <summary>
    /// Extract a clean display name from the full job name.
    /// Job names follow pattern: "{RunName}-{FunctionName}_{TenantFilter}"
    /// We want just the tenant (or meaningful identifier) portion.
    /// </summary>
    private static string ExtractTaskDisplayName(string jobName, string runName)
    {
        // Strip the run name prefix (e.g. "GraphRequestOrchestrator-guid-")
        var remaining = jobName;
        if (jobName.StartsWith(runName + "-", StringComparison.OrdinalIgnoreCase))
            remaining = jobName[(runName.Length + 1)..];

        // Strip function name prefix (e.g. "ListGraphRequestQueue_") — take everything after first underscore
        var underscoreIdx = remaining.IndexOf('_');
        if (underscoreIdx >= 0 && underscoreIdx < remaining.Length - 1)
            return remaining[(underscoreIdx + 1)..];

        return remaining;
    }

    // ── Output Models (match Get-CIPPQueueData shape) ──

    private class QueueStatusEntry
    {
        public string PartitionKey { get; set; } = "";
        public string RowKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string Link { get; set; } = "";
        public string Reference { get; set; } = "";
        public int TotalTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int RunningTasks { get; set; }
        public int FailedTasks { get; set; }
        public double PercentComplete { get; set; }
        public double PercentFailed { get; set; }
        public double PercentRunning { get; set; }
        public List<TaskDetail> Tasks { get; set; } = [];
        public string Status { get; set; } = "";
        public string Timestamp { get; set; } = "";
    }

    private class TaskDetail
    {
        public string Timestamp { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };
}
