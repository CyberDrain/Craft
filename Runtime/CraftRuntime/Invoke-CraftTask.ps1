function Invoke-CraftTask {
    <#
    .SYNOPSIS
        Generic task executor for Craft orchestrators.
    .DESCRIPTION
        Reads FunctionName from the task JSON and dispatches to Push-{FunctionName}.
        Output is captured by the C# host for result collection.
    .FUNCTIONALITY
        Internal
    #>
    [CmdletBinding()]
    param(
        [string]$TaskJson
    )

    $Item = $TaskJson | ConvertFrom-Json -AsHashtable
    $FunctionName = $Item.FunctionName

    if ([string]::IsNullOrEmpty($FunctionName)) {
        Write-Error "Task JSON missing FunctionName property: $TaskJson"
        return
    }

    $PushFunction = "Push-$FunctionName"
    $TenantLabel = $Item.TenantFilter ?? $Item.QueueName ?? $Item.defaultDomainName ?? $(if ($Item.Tenant -is [string]) { $Item.Tenant } elseif ($Item.Tenant.defaultDomainName) { $Item.Tenant.defaultDomainName } else { 'unknown' })
    # Per-task dispatch/complete are Debug: at scale (and especially during crash recovery, when every
    # pending task re-dispatches) two Info lines per task flooded the log. Push-* functions still log their
    # own meaningful output at Info; enable Debug to correlate an individual task to its tenant.
    Write-Debug "Dispatching task to $PushFunction for $TenantLabel"

    $Result = & $PushFunction -Item ([PSCustomObject]$Item)

    # Serialize output as JSON so the C# host can store structured results
    if ($null -ne $Result) {
        ConvertTo-Json -InputObject @($Result) -Depth 20 -Compress
    }

    Write-Debug "Completed task $PushFunction for $TenantLabel"
}
