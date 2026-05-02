function Start-CraftOrchestrator {
    <#
    .SYNOPSIS
        Queue an orchestrator run from PowerShell into the Craft OrchestratorService.
    .DESCRIPTION
        Generic bridge function that serializes a batch of tasks and queues them
        for fan-out execution via the C# OrchestratorService.

        This is the framework-provided default. Application-specific wrappers
        (e.g. Start-CIPPOrchestrator) can override via Orchestrator.BridgeFunction
        in appsettings.json if they need additional logic (queue routing, dual-boot, etc.)

    .PARAMETER InputObject
        Orchestrator input with the following structure:
          - OrchestratorName  (string)  — unique run identifier
          - Batch             (array)   — task objects, each with at least FunctionName
          - QueueFunction     (object)  — optional; called first to generate the batch dynamically
            - FunctionName    (string)  — Push-{FunctionName} is called
            - Parameters      (object)  — passed as -Item to the queue function
          - PostExecution     (object)  — optional; called after all tasks complete
            - FunctionName    (string)  — Push-{FunctionName} is called with aggregated results
            - Parameters      (object)  — extra parameters forwarded to the post-exec function
          - SkipLog           (bool)    — optional; suppress run logging

    .EXAMPLE
        Start-CraftOrchestrator -InputObject @{
            OrchestratorName = 'MyDataCollection'
            Batch = @(
                @{ FunctionName = 'CollectTenantData'; TenantFilter = 'contoso.com' }
                @{ FunctionName = 'CollectTenantData'; TenantFilter = 'fabrikam.com' }
            )
            PostExecution = @{ FunctionName = 'AggregateResults' }
        }

    .FUNCTIONALITY
        Internal
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$InputObject
    )

    $OrchestratorName = $InputObject.OrchestratorName ?? 'UnnamedOrchestrator'

    # QueueFunction pattern: call the function first to generate batch items
    if (-not $InputObject.Batch -and $InputObject.QueueFunction) {
        $QueueFuncName = "Push-$($InputObject.QueueFunction.FunctionName)"
        Write-Information "Craft: Calling QueueFunction '$QueueFuncName' to build batch for '$OrchestratorName'"
        $QueueItem = [PSCustomObject]@{}
        if ($InputObject.QueueFunction.Parameters) {
            $QueueItem = [PSCustomObject]$InputObject.QueueFunction.Parameters
        }
        $BatchResult = & $QueueFuncName -Item $QueueItem
        $QueueBatch = @($BatchResult | Where-Object { $null -ne $_ })
        if ($QueueBatch.Count -eq 0) {
            Write-Information "Craft: QueueFunction '$QueueFuncName' returned 0 tasks for '$OrchestratorName' - skipping"
            return "Craft-$OrchestratorName-NoTasks"
        }
        $InputObject | Add-Member -MemberType NoteProperty -Name 'Batch' -Value $QueueBatch -Force
    }

    if (-not $InputObject.Batch -or $InputObject.Batch.Count -eq 0) {
        Write-Information "Craft: No batch items for '$OrchestratorName' - skipping"
        return "Craft-$OrchestratorName-NoTasks"
    }

    $BatchJson = ConvertTo-Json -InputObject @($InputObject.Batch) -Depth 10 -Compress

    $PostExecFunctionName = $null
    $PostExecParametersJson = $null
    if ($InputObject.PostExecution) {
        $PostExecFunctionName = $InputObject.PostExecution.FunctionName
        if ($InputObject.PostExecution.Parameters) {
            $PostExecParametersJson = $InputObject.PostExecution.Parameters | ConvertTo-Json -Depth 10 -Compress
        }
    }

    Write-Information "Craft: Queuing orchestrator '$OrchestratorName' ($($InputObject.Batch.Count) tasks$(if ($PostExecFunctionName) { ", PostExec: $PostExecFunctionName" }))"
    [Craft.Services.OrchestratorBridge]::QueueOrchestration(
        $OrchestratorName,
        $BatchJson,
        4,
        $PostExecFunctionName,
        $PostExecParametersJson
    )
    return "Craft-$OrchestratorName"
}
