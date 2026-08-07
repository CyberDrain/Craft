namespace Craft.Orchestration;

/// <summary>Queued orchestration item drained by OrchestratorBridge / OrchestratorService.</summary>
internal sealed record PendingOrchestration(string Name, string BatchJson, int Priority,
    string? PostExecFunctionName, string? PostExecParametersJson, string? ParentRunName,
    string? Reference = null);

/// <summary>Queued planner-based orchestrator run.</summary>
internal sealed record PendingPlannerRun(string Command, int Priority);

/// <summary>Queued in-process background command (Add-CippQueueMessage shape).</summary>
internal sealed record PendingQueueCommand(string Cmdlet, string ParametersJson);
