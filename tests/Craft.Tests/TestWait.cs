namespace Craft.Tests;

/// <summary>
/// Shared polling helpers for async host / pump tests. Defaults are deliberately generous —
/// CI and loaded hosts routinely exceed the old 5s local-machine budgets.
/// </summary>
internal static class TestWait
{
    /// <summary>Poll until <paramref name="condition"/> is true, or the timeout elapses.</summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// Await a stop/shutdown task without hanging the suite when the pump is wedged.
    /// </summary>
    public static Task StopWithin(Task stopTask, int timeoutMs = 15_000) =>
        Task.WhenAny(stopTask, Task.Delay(timeoutMs));
}
