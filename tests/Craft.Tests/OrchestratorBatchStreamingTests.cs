using System.Reflection;
using Craft.Orchestration;

namespace Craft.Tests;

/// <summary>
/// The inbound batch is the mirror of the results aggregate: the whole fan-out used to cross the
/// PowerShell/C# boundary as ONE string, built by ConvertTo-Json on the PS side and then held a second
/// time as a JsonDocument while it was parsed. Set-CIPPDBCacheMailboxes documents what that cost —
/// every permission batch carrying a copy of all mailboxes made a 10k-mailbox tenant serialise
/// 200 batches x 10k entries into a single string.
///
/// The JSON Lines form exists so neither side ever holds more than one task. These tests pin the part
/// that has to be true for the two forms to be interchangeable: identical task lists for identical
/// input, including the ID de-duplication that the array parser does across the whole array.
///
/// Both parsers are private, so they are reached by reflection rather than by making them public for
/// the test's convenience — the surface is StartFromBatchAsync, and it should stay that way.
/// </summary>
public class OrchestratorBatchStreamingTests
{
    private static List<OrchestratorTaskItem> ParseArray(string json) =>
        Invoke("ParseTasksFromJson", json);

    private static List<OrchestratorTaskItem> ParseLines(string path) =>
        Invoke("ParseTasksFromJsonLinesFile", path);

    private static List<OrchestratorTaskItem> Invoke(string method, string arg)
    {
        // Uninitialized instance with only the one field the parsers touch. Constructing the real
        // service would drag in storage, the queue and the PS runner for a pure string-to-tasks
        // function. The logger has to be real: both parsers log on the malformed-input paths, and a
        // null one throws there rather than passing quietly.
        var svc = (OrchestratorService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrchestratorService));
        typeof(OrchestratorService)
            .GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorService>.Instance);

        var mi = typeof(OrchestratorService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(method);
        try
        {
            return (List<OrchestratorTaskItem>)mi.Invoke(svc, [arg, "run"])!;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static string WriteLines(IEnumerable<string> lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"craft-batch-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>The two delivery forms must be indistinguishable once parsed.</summary>
    [Fact]
    public void JsonLinesAndJsonArray_ProduceTheSameTasks()
    {
        string[] items =
        [
            """{"FunctionName":"CIPPStandard","Standard":"AntiPhish","TenantFilter":"a.com"}""",
            """{"FunctionName":"GetMailboxPermissionsBatch","TenantFilter":"b.com","BatchNumber":3}""",
            """{"FunctionName":"ExecCIPPDBCache","CollectionType":"Teams","TenantFilter":"c.com"}""",
            """{"FunctionName":"AuditLogDownload","Tenant":{"defaultDomainName":"d.com"}}""",
        ];

        var fromArray = ParseArray("[" + string.Join(",", items) + "]");

        var path = WriteLines(items);
        try
        {
            var fromLines = ParseLines(path);

            Assert.Equal(fromArray.Select(t => t.Id), fromLines.Select(t => t.Id));
            Assert.Equal(4, fromLines.Count);

            // Parameters survive too, not just the derived IDs.
            for (var i = 0; i < fromArray.Count; i++)
                Assert.Equal(fromArray[i].Parameters.Keys.OrderBy(k => k),
                    fromLines[i].Parameters.Keys.OrderBy(k => k));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// De-duplication is per batch, not per line. Standards items collide constantly — they all share
    /// FunctionName 'CIPPStandard' — so a per-line parser that forgot the running set would emit
    /// duplicate task IDs and the run would lose tasks to overwriting row keys.
    /// </summary>
    [Fact]
    public void CollidingTaskIds_AreDeduplicatedAcrossTheWholeFile()
    {
        var line = """{"FunctionName":"CIPPStandard","Standard":"AntiPhish","TenantFilter":"a.com"}""";
        string[] items = [line, line, line];

        var fromArray = ParseArray("[" + string.Join(",", items) + "]");

        var path = WriteLines(items);
        try
        {
            var fromLines = ParseLines(path);

            Assert.Equal(3, fromLines.Count);
            Assert.Equal(3, fromLines.Select(t => t.Id).Distinct().Count());
            Assert.Equal(fromArray.Select(t => t.Id), fromLines.Select(t => t.Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A malformed line costs that task, not the run. The array form cannot do this — one bad element
    /// fails JsonDocument.Parse and the whole batch produces zero tasks.
    /// </summary>
    [Fact]
    public void AMalformedLine_LosesOnlyThatTask()
    {
        var path = WriteLines([
            """{"FunctionName":"A","TenantFilter":"a.com"}""",
            """{"FunctionName":"B","TenantFilter":""",          // truncated
            """{"FunctionName":"C","TenantFilter":"c.com"}""",
        ]);
        try
        {
            var tasks = ParseLines(path);

            Assert.Equal(2, tasks.Count);
            Assert.Contains(tasks, t => t.Id.Contains("a.com"));
            Assert.Contains(tasks, t => t.Id.Contains("c.com"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Blank lines are padding, not tasks — a trailing newline must not invent one.</summary>
    [Fact]
    public void BlankLines_AreIgnored()
    {
        var path = WriteLines([
            """{"FunctionName":"A","TenantFilter":"a.com"}""",
            "",
            "   ",
            """{"FunctionName":"B","TenantFilter":"b.com"}""",
            "",
        ]);
        try
        {
            Assert.Equal(2, ParseLines(path).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A batch file that never arrived yields no tasks rather than throwing.</summary>
    [Fact]
    public void MissingFile_YieldsNoTasks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"craft-batch-missing-{Guid.NewGuid():N}.jsonl");
        Assert.Empty(ParseLines(path));
    }

    /// <summary>
    /// Cleanup is the caller's, on every path — including the ones that never parse.
    ///
    /// StartFromBatchAsync returns early when a run of the same name is already in progress or already
    /// active, and neither return looks at the batch. Those are the common outcome for a duplicate
    /// enqueue, so cleanup living at the parse site would leave the container's temp directory
    /// accumulating the batches of every run that was skipped rather than started. This pins the
    /// deletion to the outer method by driving it through those early returns.
    /// </summary>
    [Fact]
    public async Task BatchFileIsDeleted_EvenWhenTheRunIsSkippedWithoutParsing()
    {
        var svc = (OrchestratorService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrchestratorService));
        typeof(OrchestratorService)
            .GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorService>.Instance);

        // Mark the run as already in progress so StartFromBatchAsync takes its first early return —
        // before any storage call, so the uninitialized service is never asked for one. The field
        // initializer does not run on an uninitialized object, so the dictionary is supplied here.
        var planners = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        planners.TryAdd("busy-run", true);
        typeof(OrchestratorService).GetField("_activePlanners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, planners);

        var path = WriteLines(["""{"FunctionName":"A","TenantFilter":"a.com"}"""]);

        await svc.StartFromBatchAsync("busy-run", string.Empty, 4, null, null,
            CancellationToken.None, null, null, path);

        Assert.False(File.Exists(path),
            "the batch file outlived a skipped run — every enqueue that is skipped now leaks a temp file");
    }
}
