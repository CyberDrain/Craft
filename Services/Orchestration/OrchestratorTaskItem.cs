namespace Craft.Orchestration;

/// <summary>A task parsed from a batch or planner output, before it is stored.</summary>
public class OrchestratorTaskItem
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public Dictionary<string, object> Parameters { get; set; } = [];
}
