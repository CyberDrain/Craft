using System.Collections;

namespace Microsoft.Azure.Functions.PowerShellWorker;

/// <summary>
/// Mirror of the Azure Functions PowerShell worker's <c>HttpResponseContext</c>, compiled into
/// Craft.dll. It deliberately lives in the <c>Microsoft.Azure.Functions.PowerShellWorker</c>
/// namespace so its type name matches the real Functions worker exactly.
/// <para>
/// This type must NOT be created at runtime via <c>Add-Type -TypeDefinition</c>: the runtime
/// container image (aspnet, no SDK) ships no C# compiler, so runtime C# compilation fails and the
/// type silently never gets defined — leaving <c>[HttpResponseContext]</c> unresolvable and every
/// response coming back as <c>null</c>. Compiling it in avoids that entirely.
/// </para>
/// <para>
/// Hosted-app routers such as CIPP's <c>New-CippCoreRequest</c> pick the response object out of a
/// function's pipeline output with
/// <c>$_.PSObject.TypeNames -eq 'Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext'</c>,
/// so the namespace-qualified name has to match. <see cref="PowerShellWorker.Initialize"/> derives a
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
