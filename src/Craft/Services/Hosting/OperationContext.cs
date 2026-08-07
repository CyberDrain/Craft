namespace Craft.Hosting;

/// <summary>
/// Ambient invocation context that flows across async calls.
/// Set at operation boundaries (HTTP request, background job) and
/// read by loggers and diagnostic listeners to correlate log lines.
///
/// Mirrors Azure Functions' invocation tracking:
///   InvocationId — unique GUID per PS execution (like Functions' invocationId)
///   Function     — the PS function being invoked (e.g. "Invoke-ListUsers")
///   WorkerId     — which PS worker is executing (e.g. "W3")
///   RunName      — parent orchestrator run (e.g. "CIPPDBCacheExecute") or null for HTTP
/// </summary>
public static class OperationContext
{
    private static readonly AsyncLocal<Invocation?> s_current = new();

    /// <summary>Current invocation context (null if not in a tracked operation).</summary>
    public static Invocation? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    /// <summary>Short display string for log messages. Returns invocation ID or "—".</summary>
    public static string Display => s_current.Value?.ToString() ?? "—";

    /// <summary>Set context and return an IDisposable that restores the previous value.</summary>
    public static IDisposable Set(Invocation invocation)
    {
        var previous = s_current.Value;
        s_current.Value = invocation;
        return new Scope(previous);
    }

    private sealed class Scope(Invocation? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }

    public sealed class Invocation
    {
        /// <summary>Unique ID for this invocation (8-char hex prefix of GUID).</summary>
        public string Id { get; }

        /// <summary>The PowerShell function being executed.</summary>
        public string Function { get; }

        /// <summary>Worker identifier (e.g. "W3").</summary>
        public string? WorkerId { get; init; }

        /// <summary>Parent orchestrator run name (null for HTTP requests).</summary>
        public string? RunName { get; init; }

        /// <summary>Category: "HTTP", "Job", "Planner".</summary>
        public string Category { get; init; } = "Job";

        public Invocation(string function)
        {
            Id = Guid.NewGuid().ToString("N")[..8];
            Function = function;
        }

        /// <summary>
        /// Format: [invocation-id function W{worker} run:{run}]
        /// Examples:
        ///   "a3f2b1c0 Invoke-ListUsers W1"
        ///   "7e91d4a2 Invoke-CIPPGenericTask W3 run:CIPPDBCacheExecute"
        /// </summary>
        public override string ToString()
        {
            var parts = $"{Id} {Function}";
            if (WorkerId != null) parts += $" {WorkerId}";
            if (RunName != null) parts += $" run:{RunName}";
            return parts;
        }
    }
}
