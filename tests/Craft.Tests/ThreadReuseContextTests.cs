using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit.Abstractions;

namespace Craft.Tests;

/// <summary>
/// The worker runs its pipeline on a reused thread (<c>PSThreadOptions.ReuseThread</c>, a ~50%-of-
/// invoke-cost optimization in <c>PowerShellWorker.Initialize</c>). A reused thread keeps its
/// ExecutionContext across invocations, so an <see cref="System.Threading.AsyncLocal{T}"/> value set
/// by one invocation is visible to the next on the same worker — a real per-invocation leak. Runspace
/// SessionState (module <c>$script:</c> vars, injected caches) persists regardless of thread, so it is
/// NOT the thing that leaks and must NOT be the thing a fix clears.
///
/// This harness pins that behaviour down and compares the two candidate fixes on a raw runspace
/// configured exactly like a Craft worker, which isolates the one variable under test (thread reuse):
///   * Solution 1 — keep ReuseThread, reset the ExecutionContext each invocation (ExecutionContextReset).
///   * Solution 2 — drop ReuseThread (UseNewThread): a fresh thread ⇒ a fresh ExecutionContext.
/// Both must clear the leak AND preserve SessionState; the benchmark shows what each costs per invoke.
/// </summary>
public class ThreadReuseContextTests
{
    private readonly ITestOutputHelper _out;
    public ThreadReuseContextTests(ITestOutputHelper output) => _out = output;

    // Solution 1: run the reset on the pipeline thread as the first statement of an invocation.
    private const string ResetStatement = "[Craft.Tests.ExecutionContextReset]::ResetToClean();";
    private const string CaptureBaseline = "[Craft.Tests.ExecutionContextReset]::CaptureBaseline();";

    // Invocation A: the AsyncLocal OBJECT lives in a global (SessionState, persists like an injected
    // cache); its .Value lives in the ExecutionContext (the thing that leaks). A plain session marker
    // proves a fix leaves SessionState alone.
    private const string SetLeak = @"
        $global:__al = [System.Threading.AsyncLocal[object]]::new()
        $global:__al.Value = 'LEAKED-FROM-A'
        $global:__session = 'CACHE-FROM-A'
    ";

    // Invocation B: read both back.
    private const string ReadBack =
        "[pscustomobject]@{ AsyncLocal = $global:__al.Value; Session = $global:__session }";

    private static Runspace NewRunspace(PSThreadOptions opts)
    {
        var rs = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        rs.ThreadOptions = opts;   // must be set before Open
        rs.Open();
        return rs;
    }

    private static Collection<PSObject> Invoke(Runspace rs, string script)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(script);
        return ps.Invoke();
    }

    private static (string? asyncLocal, string? session) ReadBackValues(Runspace rs, string prefix = "")
    {
        var o = Invoke(rs, prefix + ReadBack)[0];
        return ((string?)o.Properties["AsyncLocal"]?.Value, (string?)o.Properties["Session"]?.Value);
    }

    [Fact] // A1 — establish the premise
    public void ReuseThread_LeaksAsyncLocalAcrossInvocations()
    {
        using var rs = NewRunspace(PSThreadOptions.ReuseThread);
        Invoke(rs, SetLeak);
        var (al, session) = ReadBackValues(rs);

        Assert.Equal("LEAKED-FROM-A", al);      // the leak: B sees A's AsyncLocal value
        Assert.Equal("CACHE-FROM-A", session);  // SessionState persists (expected and wanted)
    }

    [Fact] // Solution 2
    public void UseNewThread_DoesNotLeak_ButKeepsSessionState()
    {
        using var rs = NewRunspace(PSThreadOptions.UseNewThread);
        Invoke(rs, SetLeak);
        var (al, session) = ReadBackValues(rs);

        Assert.Null(al);                        // no leak: fresh thread ⇒ fresh ExecutionContext
        Assert.Equal("CACHE-FROM-A", session);  // injected/session caches survive — they are SessionState, not thread state
    }

    [Fact] // Solution 1
    public void ReuseThread_WithEcReset_DoesNotLeak_ButKeepsSessionState()
    {
        Assert.True(ExecutionContextReset.PublicRestoreAvailable,
            "ExecutionContext.Restore(ExecutionContext) not public on this runtime — S1 would need a fallback.");

        using var rs = NewRunspace(PSThreadOptions.ReuseThread);
        Invoke(rs, CaptureBaseline);   // capture the clean warmup baseline on the pipeline thread
        Invoke(rs, SetLeak);
        var (al, session) = ReadBackValues(rs, prefix: ResetStatement); // reset restores the baseline, on the pipeline thread

        Assert.Null(al);                        // reset cleared the leaked ExecutionContext
        Assert.Equal("CACHE-FROM-A", session);  // SessionState untouched by the EC reset
    }

    [Fact] // Solution 1 vs Solution 2 — the decision-relevant numbers
    public void Benchmark_ReuseVsReuseResetVsNewThread()
    {
        int warmup = EnvInt("CRAFT_BENCH_WARMUP", 200);
        int iters = EnvInt("CRAFT_BENCH_ITERS", 1500);

        var scripts = new (string name, string body)[]
        {
            ("trivial", "$null = 1"),                                             // isolates dispatch/thread/reset overhead
            ("work",    "$s=0; foreach($i in 1..200){ $s += $i }; [void]('x'*64)"), // a light real-world invocation
        };
        var modes = new (string name, PSThreadOptions opt, string prefix)[]
        {
            ("ReuseThread (leaky baseline)", PSThreadOptions.ReuseThread, ""),
            ("ReuseThread + EC reset (S1)",  PSThreadOptions.ReuseThread, ResetStatement),
            ("UseNewThread (S2)",            PSThreadOptions.UseNewThread, ""),
        };

        string mech;
        using (var probe = NewRunspace(PSThreadOptions.ReuseThread))
            mech = Invoke(probe, CaptureBaseline + "[Craft.Tests.ExecutionContextReset]::Mechanism")[0]?.ToString() ?? "?";
        _out.WriteLine($"net{Environment.Version}  publicRestore={ExecutionContextReset.PublicRestoreAvailable}  S1 reset: {mech}  warmup={warmup} iters={iters}");
        _out.WriteLine("per-invoke dispatch, reused pipeline (isolates the thread-reuse delta)  (override with CRAFT_BENCH_WARMUP / CRAFT_BENCH_ITERS)\n");
        _out.WriteLine($"{"script",-8} {"mode",-30} {"median µs",11} {"p95 µs",10} {"mean µs",10} {"vs base",8}");

        foreach (var (sname, body) in scripts)
        {
            double baseMedian = 0;
            foreach (var (mname, opt, prefix) in modes)
            {
                using var rs = NewRunspace(opt);
                using var ps = PowerShell.Create();
                ps.Runspace = rs;
                ps.AddScript(prefix + body);   // parse once; each Invoke re-runs the pipeline (new thread under UseNewThread)

                if (prefix.Length > 0) Invoke(rs, CaptureBaseline);   // S1: capture the clean baseline on the pipeline thread

                for (int i = 0; i < warmup; i++) ps.Invoke();

                var us = new double[iters];
                for (int i = 0; i < iters; i++)
                {
                    long t = Stopwatch.GetTimestamp();
                    ps.Invoke();
                    us[i] = (Stopwatch.GetTimestamp() - t) * 1_000_000.0 / Stopwatch.Frequency;
                }
                Array.Sort(us);
                double median = us[iters / 2], p95 = us[(int)(iters * 0.95)], mean = us.Average();
                if (prefix.Length == 0 && opt == PSThreadOptions.ReuseThread) baseMedian = median;
                string vs = baseMedian > 0 ? $"{median / baseMedian:0.00}x" : "-";

                _out.WriteLine($"{sname,-8} {mname,-30} {median,11:0.0} {p95,10:0.0} {mean,10:0.0} {vs,8}");
            }
            _out.WriteLine("");
        }
    }

    private static int EnvInt(string name, int dflt) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;
}
