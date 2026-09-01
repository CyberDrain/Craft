using System.Text.Json;
using Craft.Orchestration;
using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// A task id is not a label — it is written straight into a RowKey in the job queue, the tasks table
/// and the results table. Azure Table refuses '/', '\', '#', '?' and the control ranges in a key, and
/// it refuses them the same way every time, so an id carrying one is not a slow task or a flaky one:
/// the enqueue 400s, the task never leaves Pending, and the orphan re-drive retries it forever.
///
/// The case that found this was a CIPP template-library task named after its GitHub repo — the batch
/// item's QueueName was "CIPP Template Owner/Repo - No tenant", and the '/' rode straight through.
/// </summary>
public class TableKeyTests
{
    private static JsonElement Item(string json) => JsonDocument.Parse(json).RootElement;

    private static string IdFor(string json)
    {
        var tasks = new List<OrchestratorTaskItem>();
        OrchestratorService.AddTaskFromElement(tasks, new HashSet<string>(), Item(json));
        return Assert.Single(tasks).Id;
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a#b")]
    [InlineData("a?b")]
    public void SanitizeRemovesEveryCharacterTheBackendRefuses(string value)
    {
        var sanitized = TableKeys.Sanitize(value);

        Assert.True(TableKeys.IsSafe(sanitized));
        Assert.Equal("a_b", sanitized);
    }

    [Fact]
    public void SanitizeRemovesControlCharacters()
    {
        var value = "a" + (char)0x00 + "b" + (char)0x1f + "c" + (char)0x7f + "d" + (char)0x9f + "e";

        Assert.Equal("a_b_c_d_e", TableKeys.Sanitize(value));
    }

    [Fact]
    public void SanitizeLeavesASafeValueUntouched()
    {
        // Spaces, dots and hyphens are all legal in a key, and every ordinary task id is made of them —
        // returning a copy would be correct but would churn every key in the system for nothing.
        const string safe = "ExecScheduledCommand_Standards - contoso.onmicrosoft.com";

        Assert.Same(safe, TableKeys.Sanitize(safe));
    }

    [Fact]
    public void TaskIdFromARepoNamedBatchItemIsALegalKey()
    {
        var id = IdFor("""
            {
              "FunctionName": "ExecScheduledCommand",
              "QueueName": "CIPP Template Owner/Repo - No tenant"
            }
            """);

        Assert.True(TableKeys.IsSafe(id));
        Assert.Equal("ExecScheduledCommand_CIPP Template Owner_Repo - No tenant", id);
    }

    [Fact]
    public void TwoNamesThatFoldOntoTheSameIdStayDistinct()
    {
        // Sanitizing is lossy, so "Owner/Repo" and "Owner_Repo" collapse together. They must still get
        // separate rows — a shared RowKey means one task silently overwrites the other's queue entry
        // and never runs, which is a quieter failure than the 400 this replaced.
        var tasks = new List<OrchestratorTaskItem>();
        var usedIds = new HashSet<string>();

        OrchestratorService.AddTaskFromElement(tasks, usedIds,
            Item("""{"FunctionName": "Exec", "QueueName": "Owner/Repo"}"""));
        OrchestratorService.AddTaskFromElement(tasks, usedIds,
            Item("""{"FunctionName": "Exec", "QueueName": "Owner_Repo"}"""));

        Assert.Equal(2, tasks.Count);
        Assert.Distinct(tasks.Select(t => t.Id));
        Assert.All(tasks, t => Assert.True(TableKeys.IsSafe(t.Id)));
    }

    [Fact]
    public void QueueRowKeyIsLegalForARepoNamedTask()
    {
        // The end of the chain the bug actually travelled: id → BuildRowKey → upsert → 400.
        var id = IdFor("""
            {
              "FunctionName": "ExecScheduledCommand",
              "QueueName": "CIPP Template Owner/Repo - No tenant"
            }
            """);

        var rowKey = JobQueueStore.BuildRowKey("UserTaskOrchestrator_No tenant", id);

        Assert.True(TableKeys.IsSafe(rowKey));
    }
}
