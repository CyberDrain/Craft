using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Craft.Orchestration;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Run status for PowerShell and the realtime channel, without HTTP round-trips. App-neutral: a run's
/// counts, status and task list plus whatever label/link the app registered; the app shapes it for its own
/// UI. JSON is camelCase.
/// PS usage: [Craft.Services.QueueStatusBridge]::GetRun($QueueId) / ::GetRuns($Lookup)
/// </summary>
public static class QueueStatusBridge
{
    private static JobManager? s_jobManager;
    private static OrchestratorService? s_orchestratorService;
    private static JobQueueStatusReader? s_queueReader;

    /// <summary>Display metadata the app registered for a queue id or reference.</summary>
    private static readonly ConcurrentDictionary<string, RunLabel> s_labels = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(JobManager jobManager, OrchestratorService? orchestratorService = null,
        JobQueueStatusReader? queueReader = null)
    {
        s_jobManager = jobManager;
        s_orchestratorService = orchestratorService;
        s_queueReader = queueReader;
    }

    /// <summary>
    /// Register a display label and link for a queue id (and its reference), returned with its runs.
    /// PS usage: [Craft.Services.QueueStatusBridge]::RegisterQueueMetadata($QueueId, $Label, $Link, $Reference)
    /// </summary>
    public static void RegisterQueueMetadata(string queueId, string label, string link, string reference)
    {
        var meta = new RunLabel(label ?? "", link ?? "");
        if (!string.IsNullOrEmpty(queueId))
            s_labels[queueId] = meta;
        if (!string.IsNullOrEmpty(reference))
            s_labels[reference] = meta;
    }

    /// <summary>
    /// Each run matching <paramref name="lookup"/> (a run name, a reference, or a queue id the run name ends
    /// with), or every run of the last 3 hours when it is empty. JSON array of <see cref="RunStatus"/>.
    /// </summary>
    public static string GetRuns(string? lookup = null)
    {
        if (s_jobManager == null) return "[]";
        var summaries = GetMergedRunSummaries();
        if (!string.IsNullOrEmpty(lookup))
        {
            summaries = MatchRuns(summaries, lookup);
        }
        else
        {
            var cutoff = DateTime.UtcNow.AddHours(-3);
            summaries = summaries.Where(s => s.StartedUtc == null || s.StartedUtc > cutoff).ToList();
        }
        return JsonSerializer.Serialize(summaries.Select(s => ToStatus([Load(s)])).ToList(), s_json);
    }

    /// <summary>
    /// One <see cref="RunStatus"/> for everything <paramref name="lookup"/> matches, chained and child runs
    /// rolled up; JSON <c>null</c> when nothing matches yet. The same object the realtime channel pushes.
    /// </summary>
    public static string GetRun(string lookup)
    {
        if (s_jobManager == null || string.IsNullOrEmpty(lookup)) return "null";
        return GetRunRollups([lookup]).TryGetValue(lookup, out var run) ? run.Data.GetRawText() : "null";
    }

    /// <summary>
    /// A run as storage has it (which decides its status), its counts made to agree with its task list (see
    /// <see cref="Reconcile"/>), and the task list.
    /// </summary>
    private static (JobRunSummary Storage, JobRunSummary Counts, List<RunTask> Tasks) Load(JobRunSummary s)
    {
        var tasks = GetTasks(s.Name);
        return (s, Reconcile(s, tasks.Select(t => t.Status).ToList()), tasks);
    }

    /// <summary>
    /// The run's counts made to agree with its task list. Storage counts a task done only once its finish is
    /// committed, a few seconds after the job manager marks it, so a tracker showing both would have its numbers
    /// trail its per-task chips. A list holding every task gives the counts outright. A partial one (tasks not
    /// yet claimed here, a fan-out bigger than the list, a parent whose total counts its child runs) can only
    /// add: a task it shows finished is finished, so done counts take the higher of the two and the rest is
    /// queued. Only the counts: the status stays storage's, which alone knows the run (and any child it is
    /// waiting on) has finished, so a run never reports done early.
    /// </summary>
    internal static JobRunSummary Reconcile(JobRunSummary s, IReadOnlyCollection<string> taskStatuses)
    {
        if (s.Total <= 0 || taskStatuses.Count == 0 || taskStatuses.Count > s.Total) return s;
        int queued = 0, running = 0, completed = 0, failed = 0;
        foreach (var status in taskStatuses)
        {
            switch (status)
            {
                case "Queued": queued++; break;
                case "Running": running++; break;
                case "Failed": failed++; break;
                default: completed++; break;
            }
        }

        var r = new JobRunSummary
        {
            Name = s.Name,
            Reference = s.Reference,
            Priority = s.Priority,
            Total = s.Total,
            StartedUtc = s.StartedUtc,
            CompletedUtc = s.CompletedUtc
        };
        if (taskStatuses.Count == s.Total)
        {
            (r.Queued, r.Running, r.Completed, r.Failed) = (queued, running, completed, failed);
            return r;
        }

        r.Completed = Math.Max(s.Completed, completed);
        r.Failed = Math.Max(s.Failed, failed);
        r.Running = Math.Min(Math.Max(s.Running, running), s.Total - r.Completed - r.Failed);
        r.Queued = s.Total - r.Completed - r.Failed - r.Running;
        return r;
    }

    /// <summary>
    /// One status for one or more runs of the same queue: the first run's name, reference and label, the
    /// summed counts, every run's tasks, and the status of the runs together as storage has them.
    /// </summary>
    private static RunStatus ToStatus(List<(JobRunSummary Storage, JobRunSummary Counts, List<RunTask> Tasks)> runs)
    {
        var first = runs[0].Counts;
        var reference = s_orchestratorService?.GetRunReference(first.Name) ?? first.Name;
        JobRunSummary counts = new(), storage = new();
        foreach (var (st, c, _) in runs)
        {
            counts.Total += c.Total;
            counts.Queued += c.Queued;
            counts.Running += c.Running;
            counts.Completed += c.Completed;
            counts.Failed += c.Failed;
            storage.Queued += st.Queued;
            storage.Running += st.Running;
            storage.Completed += st.Completed;
            storage.Failed += st.Failed;
        }
        var label = FindLabel(reference, first.Name);
        return new RunStatus
        {
            RunName = first.Name,
            Reference = reference,
            Label = label?.Label,
            Link = label?.Link,
            Status = DeriveStatus(storage),
            Total = counts.Total,
            Queued = counts.Queued,
            Running = counts.Running,
            Completed = counts.Completed,
            Failed = counts.Failed,
            StartedUtc = runs.Min(r => r.Counts.StartedUtc),
            Tasks = runs.SelectMany(r => r.Tasks).ToList()
        };
    }

    /// <summary>The label registered for a run: by its reference, its name, or the queue id its name ends with.</summary>
    private static RunLabel? FindLabel(string reference, string runName)
    {
        if (s_labels.TryGetValue(reference, out var label) || s_labels.TryGetValue(runName, out label)) return label;
        return runName.Length > 36 && Guid.TryParse(runName[^36..], out _) && s_labels.TryGetValue(runName[^36..], out label)
            ? label
            : null;
    }

    /// <summary>
    /// Runs for one queue id or reference: exact run name, then the orchestrator's reference, then the
    /// run-name suffix (apps commonly name runs "Name-&lt;QueueId&gt;").
    /// </summary>
    private static List<JobRunSummary> MatchRuns(List<JobRunSummary> summaries, string lookup)
    {
        var matched = summaries
            .Where(s => s.Name.Equals(lookup, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matched.Count == 0 && s_orchestratorService?.FindRunByReference(lookup) is { } runName)
        {
            matched = summaries
                .Where(s => s.Name.Equals(runName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matched.Count == 0)
        {
            matched = summaries
                .Where(s => s.Name.EndsWith(lookup, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return matched;
    }

    /// <summary>
    /// What the realtime pump pushes for one queue id: its <see cref="RunStatus"/>, and a signature of its
    /// progress that changes only when the counts or a task's status do.
    /// </summary>
    internal sealed record RunRollup(string Status, JsonElement Data, string State);

    /// <summary>The rolled-up status per queue id from one read of the run summaries. Ids with no run yet are left out.</summary>
    internal static Dictionary<string, RunRollup> GetRunRollups(IReadOnlyCollection<string> ids) =>
        s_jobManager == null || ids.Count == 0
            ? new Dictionary<string, RunRollup>(StringComparer.OrdinalIgnoreCase)
            : RollUp(GetMergedRunSummaries(), ids);

    internal static Dictionary<string, RunRollup> RollUp(List<JobRunSummary> summaries, IReadOnlyCollection<string> ids)
    {
        var result = new Dictionary<string, RunRollup>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var runs = MatchRuns(summaries, id);
            if (runs.Count == 0) continue;

            var status = ToStatus(runs.Select(Load).ToList());
            var state = new StringBuilder()
                .Append(status.Status).Append('|').Append(status.Total).Append('|').Append(status.Queued)
                .Append('|').Append(status.Running).Append('|').Append(status.Completed).Append('|').Append(status.Failed);
            foreach (var t in status.Tasks) state.Append('|').Append(t.Name).Append(':').Append(t.Status);
            result[id] = new RunRollup(status.Status, JsonSerializer.SerializeToElement(status, s_json), state.ToString());
        }
        return result;
    }

    /// <summary>
    /// Run summaries sized by the durable tables where possible. The in-memory JobManager only holds
    /// the claimed slice of a run, so on its own a 7,000-task fan-out reports a Total in the dozens —
    /// and DeriveStatus reads an empty local buffer as "Completed" while thousands of rows wait
    /// unclaimed. Falls back to the local view when storage does not answer.
    /// </summary>
    private static List<JobRunSummary> GetMergedRunSummaries()
    {
        if (s_queueReader is { } reader)
        {
            try
            {
                // PS worker thread, no synchronization context — blocking is safe; the timeout keeps a
                // storage stall from wedging the worker.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                return Task.Run(() => reader.GetRunSummariesAsync(cts.Token)).GetAwaiter().GetResult();
            }
            catch
            {
                // Fall through to the local view.
            }
        }

        return s_jobManager?.GetRunSummaries() ?? [];
    }

    /// <summary>Queued, Running, Completed or CompletedWithErrors.</summary>
    private static string DeriveStatus(JobRunSummary s)
    {
        if (s.Queued == 0 && s.Running == 0)
        {
            return s.Failed > 0 ? "CompletedWithErrors" : "Completed";
        }
        if (s.Running > 0 || s.Completed > 0 || s.Failed > 0)
        {
            return "Running";
        }
        return "Queued";
    }

    /// <summary>A run's most recent tasks, each named by its job name without the run's own prefix.</summary>
    private static List<RunTask> GetTasks(string runName)
    {
        if (s_jobManager == null) return [];

        return s_jobManager.GetRunJobs(runName, limit: 100).Select(j => new RunTask
        {
            Name = j.Name.StartsWith(runName + "-", StringComparison.OrdinalIgnoreCase) ? j.Name[(runName.Length + 1)..] : j.Name,
            Status = j.Status,
            At = j.CompletedUtc ?? j.StartedUtc ?? j.QueuedUtc
        }).ToList();
    }

    // ── Output models ──

    /// <summary>A queue's runs as one status. Serialized camelCase.</summary>
    internal sealed class RunStatus
    {
        public string RunName { get; set; } = "";
        public string Reference { get; set; } = "";
        public string? Label { get; set; }
        public string? Link { get; set; }
        public string Status { get; set; } = "";
        public int Total { get; set; }
        public int Queued { get; set; }
        public int Running { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        public DateTime? StartedUtc { get; set; }
        public List<RunTask> Tasks { get; set; } = [];
    }

    internal sealed class RunTask
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime At { get; set; }
    }

    private sealed record RunLabel(string Label, string Link);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
