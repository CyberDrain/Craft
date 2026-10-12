@{
    ModuleVersion     = '1.0.0'
    GUID              = '5a0d3f4e-8c21-4b6e-9a7f-2d1c0b9e8f71'
    Author            = 'Craft.Tests'
    RootModule        = 'CraftPoolNested.psm1'
    NestedModules     = @('CraftPoolNestedInner.psm1')
    FunctionsToExport = @('Get-CraftPoolNestedThing')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
