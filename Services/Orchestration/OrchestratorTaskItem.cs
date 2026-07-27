namespace Craft.Orchestration;

public class OrchestratorTaskItem
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public Dictionary<string, object> Parameters { get; set; } = [];
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
