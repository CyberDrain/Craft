using System.Text.Json;

namespace CRAFT.Services;

/// <summary>
/// Static bridge allowing PowerShell (Get-CIPPQueueData) to query orchestrator/job
/// progress without HTTP round-trips. Returns data in the shape the CIPP frontend expects.
/// PS usage: [CRAFT.Services.QueueStatusBridge]::GetRunStatus($Reference, $QueueId)
/// </summary>
public static class QueueStatusBridge
{
    private static JobManager? s_jobManager;

    public static void Initialize(JobManager jobManager) => s_jobManager = jobManager;

    /// <summary>
    /// Get queue/run status in the format expected by the CIPP frontend.
    /// Looks up by run name (Reference) or returns all recent runs.
    /// Returns a JSON string matching the Get-CIPPQueueData output shape.
    /// </summary>
    /// <param name="reference">Optional run reference/name to filter by (maps to RunName in JobManager)</param>
    /// <param name="queueId">Optional queue ID (same as reference in CRAFT context)</param>
    /// <returns>JSON array of queue status objects</returns>
    public static string GetRunStatus(string? reference = null, string? queueId = null)
    {
        if (s_jobManager == null) return "[]";

        var lookup = queueId ?? reference;
        var summaries = s_jobManager.GetRunSummaries();

        if (!string.IsNullOrEmpty(lookup))
        {
            // Match by run name — try exact match first, then contains
            summaries = summaries
                .Where(s => s.Name.Equals(lookup, StringComparison.OrdinalIgnoreCase)
                         || s.Name.Contains(lookup, StringComparison.OrdinalIgnoreCase))
                .ToList();
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
                Reference = s.Name,
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
            Name = j.Name,
            Status = j.Status
        }).ToList();
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
