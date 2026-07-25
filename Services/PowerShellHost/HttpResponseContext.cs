using System.Collections;

namespace Microsoft.Azure.Functions.PowerShellWorker;

/// <summary>
/// Mirror of the Azure Functions PowerShell worker's <c>HttpResponseContext</c>, compiled into
/// Craft.dll. It deliberately lives in the <c>Microsoft.Azure.Functions.PowerShellWorker</c>
/// namespace so its type name matches the real Functions worker exactly.
/// <para>
/// This type is compiled in rather than defined at runtime via <c>Add-Type -TypeDefinition</c>.
/// Runtime compilation does work in the container image — Roslyn and the <c>ref/</c> reference
/// assemblies are both shipped deliberately for exactly that reason (see build/Dockerfile) — but
/// relying on it here would be the wrong trade. This type is on the response path of every single
/// request, so it must exist before the first script runs, in every runspace, with no dependency on
/// a compilation step that could fail at startup. Compiling it in also avoids paying a Roslyn
/// compile on each runspace that touches it.
/// </para>
/// <para>
/// Historical note, because the previous comment here asserted the opposite and sent people the
/// wrong way: it claimed the image "ships no C# compiler". That was never true. Roslyn is present
/// and loadable. What had actually been removed at one point was <c>ref/</c>, which made
/// <c>Add-Type -TypeDefinition</c> fail with <c>DirectoryNotFoundException: '/app/ref'</c> while the
/// compiler itself sat there working fine.
/// </para>
/// <para>
/// Hosted-app routers such as CIPP's <c>New-CippCoreRequest</c> pick the response object out of a
/// function's pipeline output with
/// <c>$_.PSObject.TypeNames -eq 'Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext'</c>,
/// so the namespace-qualified name has to match. <see cref="Craft.PowerShellHost.PowerShellWorker.Initialize"/> derives a
/// runspace-level <c>[HttpResponseContext]</c> PowerShell class from this type so scripts resolve
/// the short name (PS classes are compiled by the PS engine, no Roslyn) while instances still carry
/// this base type's name in their <c>PSTypeNames</c>.
/// </para>
/// </summary>
public class HttpResponseContext
{
    public object StatusCode { get; set; } = 200;
    public object? Body { get; set; }
    public Hashtable Headers { get; set; } = new();
    public string ContentType { get; set; } = "application/json";
}
