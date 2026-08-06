using Craft.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;

namespace Craft.Tests;

/// <summary>
/// Native scheduled tasks: the contract, and the discovery rules that keep a timer file honest.
/// <para>
/// The timer file names a <c>Command</c> and nothing else — which code answers to it is resolved at
/// startup, native registry first, PowerShell script table second. Everything here defends the two
/// properties that resolution depends on: a Command maps to at most one native task, and lookup is
/// case-insensitive the way PowerShell command names already are.
/// </para>
/// </summary>
public class NativeScheduledTaskTests
{
    [CraftScheduledTask("SslCheckRun")]
    private sealed class SslCheckTask : ICraftScheduledTask
    {
        public Task RunAsync(CraftTaskContext context, CancellationToken ct) => Task.CompletedTask;
    }

    [CraftScheduledTask("sslcheckrun")]
    private sealed class CaseInsensitiveDuplicateTask : ICraftScheduledTask
    {
        public Task RunAsync(CraftTaskContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AttributelessTask : ICraftScheduledTask
    {
        public Task RunAsync(CraftTaskContext context, CancellationToken ct) => Task.CompletedTask;
    }

    // ── the attribute ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommandIsTrimmed()
    {
        // The Command is compared against a hand-authored JSON file; stray whitespace in either
        // place must not make a timer silently never match.
        Assert.Equal("Nightly", new CraftScheduledTaskAttribute(" Nightly ").Command);
    }

    [Fact]
    public void EmptyCommandIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new CraftScheduledTaskAttribute("  "));
    }

    // ── discovery ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TasksAreDescribedFromTheirAttribute()
    {
        var tasks = NativeEndpointRegistry.BuildScheduledTasks(
            [typeof(SslCheckTask)], NullLogger.Instance);

        var task = Assert.Single(tasks);
        Assert.Equal("SslCheckRun", task.Command);
        Assert.Equal(typeof(SslCheckTask), task.ImplementationType);
    }

    [Fact]
    public void AttributelessImplementationIsSkippedNotThrown()
    {
        // No Command means no timer can ever reach it. That is a mistake worth a log line, but not
        // one worth failing the deploy over — the rest of the assembly still works.
        Assert.Empty(NativeEndpointRegistry.BuildScheduledTasks(
            [typeof(AttributelessTask)], NullLogger.Instance));
    }

    [Fact]
    public void DuplicateCommandsFailStartupEvenAcrossCase()
    {
        // Which implementation the timer fired would be assembly scan order. Case-insensitive
        // because the scheduler's lookup is — two names that collide at lookup time collide here.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NativeEndpointRegistry.BuildScheduledTasks(
                [typeof(SslCheckTask), typeof(CaseInsensitiveDuplicateTask)], NullLogger.Instance));

        Assert.Contains("SslCheckRun", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── the scheduler's lookup ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyLookupSaysNoToEverything()
    {
        Assert.Equal(0, NativeScheduledTasks.Empty.Count);
        Assert.False(NativeScheduledTasks.Empty.TryGet("Anything", out _));
    }

    [Fact]
    public void LookupIsCaseInsensitiveLikePowerShellCommands()
    {
        var lookup = new NativeScheduledTasks(NativeEndpointRegistry.BuildScheduledTasks(
            [typeof(SslCheckTask)], NullLogger.Instance));

        Assert.True(lookup.TryGet("SSLCHECKRUN", out var descriptor));
        Assert.Equal(typeof(SslCheckTask), descriptor.ImplementationType);
    }

    // ── the context ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContextRoundTripsWhatTheTimerDeclared()
    {
        // Public constructor on purpose: application test suites build these directly to exercise
        // their tasks without a scheduler.
        var parameters = new Dictionary<string, object> { ["Threshold"] = 30 };
        var fireTime = new DateTimeOffset(2026, 8, 2, 3, 0, 0, TimeSpan.Zero);

        var context = new CraftTaskContext("timer-1", "SslCheckRun", parameters, fireTime);

        Assert.Equal("timer-1", context.TimerId);
        Assert.Equal("SslCheckRun", context.Command);
        Assert.Same(parameters, context.Parameters);
        Assert.Equal(fireTime, context.FireTimeUtc);
    }
}
