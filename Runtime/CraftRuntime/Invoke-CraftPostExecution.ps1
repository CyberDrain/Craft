function Invoke-CraftPostExecution {
    <#
    .SYNOPSIS
        PostExecution dispatcher for CRAFT orchestrators.
    .DESCRIPTION
        Receives collected task results as JSON from the C# host,
        builds the $Item (FunctionName, Results, Parameters),
        and calls Push-{FunctionName}.
    .FUNCTIONALITY
        Internal
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FunctionName,

        [Parameter(Mandatory)]
        [string]$ResultsJson,

        [string]$ParametersJson
    )

    $ResultsList = [System.Collections.Generic.List[object]]::new()
    try {
        $Parsed = $ResultsJson | ConvertFrom-Json -AsHashtable
        foreach ($Entry in @($Parsed)) {
            $ResultsList.Add($Entry)
        }
    } catch {
        Write-Warning "Failed to parse ResultsJson: $_"
    }

    Write-Information "PostExecution: $($ResultsList.Count) results for Push-$FunctionName"

    $Item = [PSCustomObject]@{
        FunctionName = $FunctionName
        Results      = $ResultsList
    }

    if ($ParametersJson) {
        $Parameters = $ParametersJson | ConvertFrom-Json
        $Item | Add-Member -NotePropertyName 'Parameters' -NotePropertyValue $Parameters
    }

    $PushFunction = "Push-$FunctionName"
    Write-Information "PostExecution: Calling $PushFunction with $($ResultsList.Count) results"
    & $PushFunction -Item $Item
    Write-Information "PostExecution: $PushFunction completed"
}
