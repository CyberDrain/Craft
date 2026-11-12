function Invoke-ExecBackendProcess {
    <#
    .SYNOPSIS
        List or trigger scheduled background tasks.
    .DESCRIPTION
        Framework endpoint for the Craft scheduler. Lists tasks from the scheduler
        config or fires one now through the scheduler, exactly as its schedule would.

        GET /api/ExecBackendProcess              — List all scheduled tasks
        GET /api/ExecBackendProcess?Action=Start&Command=TaskName — Trigger a task
    .FUNCTIONALITY
        Entrypoint
    #>
    [CmdletBinding()]
    param($Request, $TriggerMetadata)

    $APIName = $Request.Params.CIPPEndpoint
    $Action = $Request.Query.Action
    $Command = $Request.Query.Command

    try {
        # Load scheduled task definitions from the resolved scheduler config path
        $ConfigPath = $env:CRAFT_SCHEDULER_CONFIG
        $AllTasks = if ($ConfigPath -and (Test-Path $ConfigPath)) {
            Get-Content $ConfigPath -Raw | ConvertFrom-Json
        } else { @() }

        switch ($Action) {
            'Start' {
                if ([string]::IsNullOrEmpty($Command)) {
                    $Body = @{ error = 'Command parameter is required' }
                    $StatusCode = 400
                } else {
                    # Match by exact Command, or with Start- prefix, or by suffix
                    $Task = $AllTasks | Where-Object {
                        $_.Command -eq $Command -or
                        $_.Command -eq "Start-$Command" -or
                        $_.Command -like "*-$Command"
                    } | Select-Object -First 1
                    if (-not $Task) {
                        $Body = @{ error = "Task '$Command' not found in scheduler config" }
                        $StatusCode = 404
                    } elseif (-not [Craft.Services.SchedulerBridge]::RunNow($Task.Command)) {
                        $Body = @{ error = "Task '$($Task.Command)' is not loaded by the scheduler" }
                        $StatusCode = 404
                    } else {
                        $Body = @{
                            command  = $Task.Command
                            priority = $Task.Priority
                            message  = "'$($Task.Command)' queued at priority $($Task.Priority)"
                        }
                        $StatusCode = 200
                        Write-LogMessage -API $APIName -message "Backend process '$($Task.Command)' triggered (P$($Task.Priority))" -sev Info
                    }
                }
            }
            default {
                # List all scheduled tasks (orchestrators + simple scripts) with priority info
                $Body = @($AllTasks | ForEach-Object {
                        $IsOrch = $_.IsOrchestratorOverride -eq $true
                        @{
                            id          = $_.Id
                            command     = $_.Command
                            description = $_.Description
                            cron        = $_.Cron
                            priority    = $_.Priority
                            type        = if ($IsOrch) { 'Orchestrator' } else { 'Script' }
                        }
                    })
                $StatusCode = 200
            }
        }
    } catch {
        $ErrorMessage = Get-CippException -Exception $_
        Write-LogMessage -API $APIName -message "Backend process error: $($ErrorMessage.NormalizedError)" -sev Error -LogData $ErrorMessage
        $Body = @{ error = $ErrorMessage.NormalizedError }
        $StatusCode = 500
    }

    return ([HttpResponseContext]@{
            StatusCode = $StatusCode
            Body       = $Body
        })
}
