function Start-CraftOrchestrator {
    <#
    .SYNOPSIS
        Queue an orchestrator run from PowerShell into the Craft OrchestratorService.
    .DESCRIPTION
        Generic bridge function that serializes a batch of tasks and queues them
        for fan-out execution via the C# OrchestratorService.

        This is the framework-provided default. Applications with additional logic
        (queue routing, dual-boot, etc.) ship their own wrapper (e.g. Start-CIPPOrchestrator)
        and simply call that instead of this function.

    .PARAMETER InputObject
        Orchestrator input with the following structure:
          - OrchestratorName  (string)  — unique run identifier
          - Batch             (array)   — task objects, each with at least FunctionName
          - Priority          (int)     — optional; queue priority bucket for the run (lower = sooner).
                                          Defaults to the parent run's priority when queued from inside
                                          an orchestrator run, else 4.
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
        $QueueItem = [PSCustomObject]$InputObject.QueueFunction
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

    $PostExecFunctionName = $null
    $PostExecParametersJson = $null
    if ($InputObject.PostExecution) {
        $PostExecFunctionName = $InputObject.PostExecution.FunctionName
        if ($InputObject.PostExecution.Parameters) {
            $PostExecParametersJson = $InputObject.PostExecution.Parameters | ConvertTo-Json -Depth 10 -Compress
        }
    }

    # Write the batch as JSON Lines — one task per line — and hand the orchestrator the path rather
    # than the content. Serialising the whole array into one string makes the peak allocation scale
    # with the size of the fan-out, which is exactly the case that hurts: a run big enough to be worth
    # fanning out is a run whose batch string is big. One task at a time costs one task, at any size.
    # ConvertTo-Json -Compress escapes newlines, so a task is always exactly one line.
    $BatchPath = Join-Path ([System.IO.Path]::GetTempPath()) "craft-batch-$([guid]::NewGuid().ToString('N')).jsonl"
    $TaskCount = 0
    try {
        $Writer = [System.IO.StreamWriter]::new($BatchPath, $false, [System.Text.Encoding]::UTF8)
        try {
            foreach ($BatchItem in @($InputObject.Batch)) {
                if ($null -eq $BatchItem) { continue }
                $Writer.WriteLine((ConvertTo-Json -InputObject $BatchItem -Depth 10 -Compress))
                $TaskCount++
            }
        } finally {
            $Writer.Dispose()
        }
    } catch {
        # Queue nothing on a partial write — a half-written batch would start a run silently missing
        # tasks. Drop the file and surface the failure instead.
        Remove-Item -LiteralPath $BatchPath -Force -ErrorAction SilentlyContinue
        Write-Error "Failed to write batch file for '$OrchestratorName': $($_.Exception.Message)"
        throw
    }

    # Priority resolution: explicit on the InputObject wins; otherwise inherit the enclosing run's
    # priority (stamped into the global CraftOperationContext variable by the worker — the pipeline
    # thread never sees OperationContext.Current directly); otherwise the default band. Guard the
    # explicit value — the store clamps into 0-99 buckets, so a stray negative would silently land
    # in the critical P00 bucket.
    $Priority = $InputObject.Priority
    if ($null -ne $Priority) {
        $Priority = [int]$Priority
        if ($Priority -lt 0 -or $Priority -gt 99) { $Priority = $null }
    }
    if ($null -eq $Priority) {
        $OpContext = Get-Variable -Name 'CraftOperationContext' -Scope Global -ValueOnly -ErrorAction SilentlyContinue
        $Priority = $OpContext.Priority ?? 4
    }

    Write-Information "Craft: Queuing orchestrator '$OrchestratorName' ($TaskCount tasks, P$Priority$(if ($PostExecFunctionName) { ", PostExec: $PostExecFunctionName" }))"
    [Craft.Services.OrchestratorBridge]::QueueOrchestrationFromFile(
        $OrchestratorName,
        $BatchPath,
        $Priority,
        $PostExecFunctionName,
        $PostExecParametersJson,
        $InputObject.Reference
    )
    return "Craft-$OrchestratorName"
}
