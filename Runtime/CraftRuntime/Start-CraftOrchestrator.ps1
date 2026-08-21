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

    # The stamped operation context is the only way this function can see the enclosing run — the
    # pipeline thread never sees OperationContext.Current (frozen ExecutionContext under
    # ReuseThread; see PowerShellWorker.StampOperationContext) — so both the priority default and
    # the parent-run lineage below must come from this variable.
    $OpContext = Get-Variable -Name 'CraftOperationContext' -Scope Global -ValueOnly -ErrorAction SilentlyContinue

    # Priority resolution: explicit on the InputObject wins when it is a valid bucket; otherwise
    # inherit the enclosing run's priority; otherwise the default band. Out-of-range values take
    # the fallback too — the store clamps into 0-99 buckets, so a stray negative would otherwise
    # silently land in the critical P00 bucket.
    $Priority = if ($null -ne $InputObject.Priority) { [int]$InputObject.Priority }
    if ($null -eq $Priority -or $Priority -lt 0 -or $Priority -gt 99) {
        $Priority = $OpContext.Priority ?? 4
    }

    # Lineage: pass the enclosing run explicitly. The bridge's own ambient read is null for calls
    # made from the pipeline thread — which is exactly where this function runs — so without this
    # a parent run would finalize (and dispatch its PostExecution) before its child runs complete.
    $ParentRunName = $OpContext.RunName

    Write-Information "Craft: Queuing orchestrator '$OrchestratorName' ($TaskCount tasks, P$Priority$(if ($PostExecFunctionName) { ", PostExec: $PostExecFunctionName" })$(if ($ParentRunName) { ", Parent: $ParentRunName" }))"
    [Craft.Services.OrchestratorBridge]::QueueOrchestrationFromFile(
        $OrchestratorName,
        $BatchPath,
        $Priority,
        $PostExecFunctionName,
        $PostExecParametersJson,
        $InputObject.Reference,
        $ParentRunName
    )
    return "Craft-$OrchestratorName"
}
