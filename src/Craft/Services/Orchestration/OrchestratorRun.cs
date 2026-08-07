namespace Craft.Orchestration;

public class OrchestratorRun
{
    public string Name { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string Status { get; set; } = "Pending";
    public int Priority { get; set; } = 2;
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public List<OrchestratorTaskItem> Tasks { get; set; } = [];
    public string? TaskScriptName { get; set; }
    public string? PostExecFunctionName { get; set; }
    public string? PostExecParametersJson { get; set; }
    public string? PostExecStatus { get; set; }  // null | "Pending" | "Running" | "Completed" | "Failed"
    public string? ParentRunName { get; set; }
}
