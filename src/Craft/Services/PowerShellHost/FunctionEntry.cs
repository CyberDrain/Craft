using System.Management.Automation;

namespace Craft.PowerShellHost;

public class FunctionEntry
{
    public required string FunctionName { get; init; }
    public required ScriptBlock ScriptBlock { get; init; }
    public required string SourcePath { get; init; }
    public required FunctionCategory Category { get; init; }
}
