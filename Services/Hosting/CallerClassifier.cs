namespace Craft.Hosting;

/// <summary>
/// Classifies a request as an app-only API client versus an interactive (UI) caller, from the
/// normalised principal headers <see cref="CraftAuthMiddleware"/> writes.
/// <para>
/// The distinction is load-bearing for the API concurrency cap: app-only automation must not be able
/// to monopolise the shared worker pool and starve interactive users, who are never limited by it.
/// The rule is exactly the one the hosted app already keys off — a client-credentials caller arrives
/// with <c>x-ms-client-principal-idp: aad</c> and its AppId (a GUID) as the principal name, whereas an
/// interactive Entra user is normalised to <c>azureStaticWebApps</c>. Both conditions are required so a
/// stray <c>aad</c> idp on a non-GUID principal is never misread as an API client.
/// </para>
/// </summary>
public static class CallerClassifier
{
    /// <summary>
    /// True when <paramref name="context"/> is an app-only API client (idp is <c>aad</c> and the
    /// principal name parses as a GUID AppId). Depends on running after <see cref="CraftAuthMiddleware"/>.
    /// </summary>
    public static bool IsApiClient(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var idp = context.Request.Headers["x-ms-client-principal-idp"].ToString();
        if (!string.Equals(idp, "aad", StringComparison.OrdinalIgnoreCase)) return false;

        var name = context.Request.Headers["x-ms-client-principal-name"].ToString();
        return Guid.TryParse(name, out _);
    }
}
