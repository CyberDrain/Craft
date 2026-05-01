function Invoke-CraftTask {
    <#
    .SYNOPSIS
        Generic task executor for CRAFT orchestrators.
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
    Write-Information "Dispatching task to $PushFunction for $($Item.TenantFilter ?? 'unknown')"

    $Result = & $PushFunction -Item ([PSCustomObject]$Item)

    # Serialize output as JSON so the C# host can store structured results
    if ($null -ne $Result) {
        ConvertTo-Json -InputObject @($Result) -Depth 20 -Compress
    }

    Write-Information "Completed task $PushFunction for $($Item.TenantFilter ?? 'unknown')"
}
