@{
    RootModule        = 'PerfApi.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b7e6c1a2-9f34-4c88-8a11-0f0f0f0f0f01'
    Author            = 'CRAFT perf-harness'
    Description       = 'Synthetic HTTP endpoints for load-testing CRAFT in http-only mode. Not for production.'
    PowerShellVersion = '7.2'
    FunctionsToExport = @('Invoke-PerfPing', 'Invoke-PerfEcho', 'Invoke-PerfCpu', 'Invoke-PerfSleep', 'Invoke-PerfJson', 'Invoke-PerfBgEnqueue', 'Push-PerfBg', 'Push-PerfBgLeaf', 'Invoke-ListPerf', 'Invoke-PerfWhoami', 'Invoke-PerfTimerTick', 'Invoke-PerfTimerCount', 'Invoke-PerfPublish', 'Invoke-PerfAllocation', 'Invoke-PerfRuns')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
