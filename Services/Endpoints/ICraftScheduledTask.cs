
namespace Craft.Endpoints;

/// <summary>
/// A scheduled task written in C# rather than PowerShell, fired by the same scheduler from the same
/// timer file. The timer entry's <c>Command</c> is matched against
/// <see cref="CraftScheduledTaskAttribute.Command"/> before the PowerShell script table, so an
/// all-native application schedules background work without deploying a single script — and with
/// <c>Worker:BgPoolSize=0</c>, without building a single runspace.
///
/// <para>
/// The task runs on the .NET thread pool through the same <c>JobManager</c> the PowerShell path
/// uses, so priority ordering, job records and the background concurrency limiter apply unchanged.
/// What does NOT apply is the orchestrator: its planner/task split is a PowerShell construct, and
/// native code that wants fan-out writes <c>Task.WhenAll</c> over a <c>SemaphoreSlim</c> instead. A
/// timer that names a native task and sets <c>IsOrchestratorOverride</c> is rejected at load.
/// </para>
///
/// <para>
/// One semantic difference from PowerShell to design for: <c>Worker:BgTimeoutSeconds</c> stops a PS
/// pipeline forcibly, but a native task is cancelled <i>cooperatively</i> — the token fires and a
/// task that never observes it runs on. Pass the token to everything that accepts one.
/// </para>
///
/// Implementations are resolved from DI per firing and are singletons by default, matching endpoints.
/// </summary>
public interface ICraftScheduledTask
{
    /// <summary>Runs one firing of the task.</summary>
    Task RunAsync(CraftTaskContext context, CancellationToken ct);
}

/// <summary>
/// Marks a class as a native scheduled task and names the timer <c>Command</c> that fires it.
/// Declared explicitly rather than derived from the type name for the same reason endpoint routes
/// are: migrating a task from PowerShell must not change what the timer file says.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CraftScheduledTaskAttribute : Attribute
{
    public CraftScheduledTaskAttribute(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Command = command.Trim();
    }

    /// <summary>The timer file <c>Command</c> this task answers to. Matched case-insensitively,
    /// like PowerShell script names.</summary>
    public string Command { get; }

    /// <summary>Singleton by default, for the same reasons an endpoint is — see
    /// <see cref="CraftEndpointAttribute.Lifetime"/>.</summary>
    public ServiceLifetime Lifetime { get; init; } = ServiceLifetime.Singleton;
}

/// <summary>
/// What a native task knows about the firing that invoked it. Public constructor on purpose:
/// application test suites build these directly.
/// </summary>
public sealed class CraftTaskContext
{
    public CraftTaskContext(
        string timerId,
        string command,
        IReadOnlyDictionary<string, object>? parameters,
        DateTimeOffset fireTimeUtc)
    {
        TimerId = timerId;
        Command = command;
        Parameters = parameters;
        FireTimeUtc = fireTimeUtc;
    }

    /// <summary>The timer entry's <c>Id</c>.</summary>
    public string TimerId { get; }

    /// <summary>The timer entry's <c>Command</c> — useful when one class serves several timers.</summary>
    public string Command { get; }

    /// <summary>
    /// The timer entry's <c>Parameters</c>, if any. Values are whatever
    /// <c>System.Text.Json</c> produced from the hand-authored timer file (i.e.
    /// <see cref="System.Text.Json.JsonElement"/>), not CLR primitives.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Parameters { get; }

    /// <summary>When the scheduler decided this task was due.</summary>
    public DateTimeOffset FireTimeUtc { get; }
}

/// <summary>One discovered native scheduled task.</summary>
public sealed record NativeTaskDescriptor(
    string Command,
    Type ImplementationType,
    CraftScheduledTaskAttribute Metadata);

/// <summary>
/// The scheduler's lookup of native tasks by <c>Command</c>. Registered empty by the host and
/// replaced with the discovered set when the application ships any (last registration wins), so the
/// scheduler can always inject it.
/// </summary>
public sealed class NativeScheduledTasks
{
    /// <summary>The no-native-tasks instance every deployment starts from.</summary>
    public static NativeScheduledTasks Empty { get; } = new([]);

    private readonly Dictionary<string, NativeTaskDescriptor> _byCommand;

    public NativeScheduledTasks(IReadOnlyList<NativeTaskDescriptor> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        _byCommand = new Dictionary<string, NativeTaskDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            // Duplicates are rejected at discovery; this add is therefore unconditional so a bug
            // there surfaces here as a throw rather than as a silently-last-wins lookup.
            _byCommand.Add(task.Command, task);
        }
    }

    public int Count => _byCommand.Count;

    public bool TryGet(string command, out NativeTaskDescriptor descriptor) =>
        _byCommand.TryGetValue(command, out descriptor!);
}
