@{
    RootModule        = 'PerfNested.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b7e6c1a2-9f34-4c88-8a11-0f0f0f0f0f02'
    Author            = 'CRAFT perf-harness'
    Description       = 'A module with NestedModules, which workers import through the InitialSessionState rather than share.'
    NestedModules     = @('PerfNestedInner.psm1')
    FunctionsToExport = @('Get-PerfNestedThing')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
