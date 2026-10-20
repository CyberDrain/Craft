using Craft.Services;

namespace Craft.Tests;

/// <summary>
/// Runs of the same query share a reference, and another user may start one after yours: a run must keep the label
/// and link registered for its own queue id, not take the latest one registered under the reference.
/// </summary>
public class QueueStatusBridgeLabelTests
{
    [Fact]
    public void ARunsOwnQueueId_WinsOverItsSharedReference()
    {
        var mine = Guid.NewGuid().ToString();
        var later = Guid.NewGuid().ToString();
        var reference = $"AllTenants-{Guid.NewGuid():N}";
        QueueStatusBridge.RegisterQueueMetadata(mine, "Users (All Tenants)", "/mine", reference);
        QueueStatusBridge.RegisterQueueMetadata(later, "Users (133 missing tenants)", "/later", reference);

        var label = QueueStatusBridge.FindLabel(reference, $"GraphRequestOrchestrator-{mine}");

        Assert.Equal(("Users (All Tenants)", "/mine"), (label?.Label, label?.Link));
    }

    [Fact]
    public void ARunWithNoQueueId_StillTakesItsReferencesLabel()
    {
        var reference = $"Ref-{Guid.NewGuid():N}";
        QueueStatusBridge.RegisterQueueMetadata("", "Nightly sync", "/sync", reference);

        Assert.Equal("Nightly sync", QueueStatusBridge.FindLabel(reference, "NightlySync")?.Label);
    }
}
