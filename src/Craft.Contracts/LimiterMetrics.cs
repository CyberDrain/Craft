// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

public class LimiterMetrics
{
    public int BaseConcurrency { get; set; }
    public int CeilingConcurrency { get; set; }
    public int CurrentMax { get; set; }
    public int Active { get; set; }
    public int Waiting { get; set; }
    public bool IsHttpThrottled { get; set; }
}
