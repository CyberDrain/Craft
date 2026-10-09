function Get-CraftPoolFixtureThing {
    [CmdletBinding()]
    param([string]$TenantFilter)
    "thing:$TenantFilter"
}

function Wait-CraftPoolFixtureGate {
    # Blocks in a .NET wait, which a pipeline stop request cannot interrupt (as a stuck lock or socket read would).
    param($Request, $TriggerMetadata, [string]$GateKey)
    if (-not $GateKey) { $GateKey = $Request.GateKey }
    $null = [Craft.Services.PowerShellRunnerService]::GetSharedCache('CraftPoolFixture')[$GateKey].Wait()
}
