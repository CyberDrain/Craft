function Invoke-CraftPostExecution {
    <#
    .SYNOPSIS
        PostExecution dispatcher for Craft orchestrators.
    .DESCRIPTION
        Receives the path to the collected task results from the C# host,
        builds the $Item (FunctionName, Results, Parameters),
        and calls Push-{FunctionName}.

        The results arrive as a file path rather than as a string. The host writes one result per
        line (JSON Lines) and this walks it with File.ReadLines, so only ONE result is held as text
        at a time. Taking the aggregate as a string instead — as this used to — put the whole thing
        on the Large Object Heap and then had ConvertFrom-Json copy it again; for a 738-task run that
        is 50-150MB materialised twice, against a 2398MB heap cap, concurrently per run.

        $Item.Results has the same shape it always did: one entry per task, each entry being whatever
        that task's JSON deserialised to. Push-* consumers need no changes.
    .PARAMETER FunctionName
        The name of the Push-* function to dispatch to.
    .PARAMETER ResultsPath
        Path to the JSON Lines file of task results written by the host. One result per line.
    .PARAMETER ParametersJson
        Optional extra parameters, forwarded to the Push-* function as $Item.Parameters.
    .FUNCTIONALITY
        Internal
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FunctionName,

        [Parameter(Mandatory)]
        [string]$ResultsPath,

        [string]$ParametersJson
    )

    $ResultsList = [System.Collections.Generic.List[object]]::new()
    $LineNumber = 0
    $FailedLines = 0

    if (Test-Path -LiteralPath $ResultsPath) {
        # ReadLines is lazy — it does NOT read the file into memory. Each line is parsed and released
        # before the next is read, so peak text held is one result rather than the whole aggregate.
        foreach ($Line in [System.IO.File]::ReadLines($ResultsPath)) {
            $LineNumber++
            if ([string]::IsNullOrWhiteSpace($Line)) { continue }

            try {
                # Parsed as a one-element array so the deserialised shape is identical to the old
                # whole-aggregate parse. ConvertFrom-Json unrolls a top-level array into the pipeline,
                # which would otherwise collapse a single-element result like [{...}] into a bare
                # object — and consumers that read .Count would then see the object's property count
                # instead of 1.
                foreach ($Entry in @("[$Line]" | ConvertFrom-Json -AsHashtable)) {
                    $ResultsList.Add($Entry)
                }
            } catch {
                # One malformed result costs that result, not the run. The old whole-aggregate parse
                # failed closed: a single bad result left Push-* with nothing at all.
                $FailedLines++
                if ($FailedLines -le 3) {
                    Write-Warning "PostExecution: failed to parse result on line $LineNumber of $ResultsPath : $($_.Exception.Message)"
                }
            }
        }
    } else {
        Write-Warning "PostExecution: results file not found: $ResultsPath"
    }

    if ($FailedLines -gt 0) {
        Write-Warning "PostExecution: $FailedLines of $LineNumber result(s) failed to parse for Push-$FunctionName"
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
