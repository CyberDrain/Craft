#pragma warning disable CA1050 // PowerShell resolves [HttpResponseContext] by this exact global name.
/// <summary>
/// The short-named <c>[HttpResponseContext]</c> scripts construct. Compiled once here rather than as a PowerShell class in
/// every runspace, which recompiled it on every worker build; instances keep the same <c>PSTypeNames</c> as that class:
/// <c>HttpResponseContext</c>, then <see cref="Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext"/>.
/// </summary>
public class HttpResponseContext : Microsoft.Azure.Functions.PowerShellWorker.HttpResponseContext;
