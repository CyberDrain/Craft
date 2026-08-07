namespace Craft.Orchestration;

/// <summary>
/// In-memory job tracking record used by <see cref="JobManager"/>.
/// Projected to public <c>JobDetail</c> / API JSON before leaving the host.
/// </summary>
internal sealed class JobRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? RunName { get; set; }
    public int Priority { get; set; }
    public string Status { get; set; } = "Queued";
    public DateTime QueuedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? LastError { get; set; }
}
