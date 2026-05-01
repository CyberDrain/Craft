function Invoke-CraftQueueTask {
    <#
    .SYNOPSIS
        Executes a queued command on a CRAFT background worker.
    .DESCRIPTION
        Receives a cmdlet name and JSON parameters, validates the command exists
        in an allowed module, and executes it.
    .FUNCTIONALITY
        Internal
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Cmdlet,

        [string]$ParametersJson = '{}'
    )

    # Validate command exists in allowed modules (configured via CRAFT_ALLOWED_MODULES env var)
    $AllowedModules = if ($env:CRAFT_ALLOWED_MODULES) {
        $env:CRAFT_ALLOWED_MODULES -split ','
    } else {
        @()
    }

    $ValidModule = $false
    if ($AllowedModules.Count -eq 0) {
        # No allowlist configured — accept any available command
        if (Get-Command -Name $Cmdlet -ErrorAction SilentlyContinue) {
            $ValidModule = $true
        }
    } else {
        foreach ($Module in $AllowedModules) {
            if (Get-Command -Name $Cmdlet -Module $Module.Trim() -ErrorAction SilentlyContinue) {
                $ValidModule = $true
                break
            }
        }
    }

    if (-not $ValidModule) {
        Write-Warning "Queue: Command not found in any allowed module: $Cmdlet. Skipping."
        return
    }

    $Parameters = $ParametersJson | ConvertFrom-Json -AsHashtable

    Write-Information "Queue: Executing $Cmdlet"
    try {
        & $Cmdlet @Parameters
    } catch {
        Write-Warning "Queue: Error in $Cmdlet : $_"
    }
    Write-Information "Queue: Completed $Cmdlet"
}
