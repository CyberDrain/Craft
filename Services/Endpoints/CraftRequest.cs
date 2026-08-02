using System.Text.Json;
using Craft.Auth;

namespace Craft.Endpoints;

/// <summary>
/// The request as a native endpoint sees it: a <b>lazy view over <see cref="HttpContext"/></b>, not a
/// copy of it.
///
/// <para>
/// This is deliberately NOT the <c>$Request</c> hashtable the PowerShell path builds. That shape
/// exists because PowerShell needs everything pre-marshalled into hashtables it can index — query and
/// headers copied, header names lower-cased, the body parsed into a PSObject graph — and building it
/// is work every request pays whether or not the handler looks at any of it. Symmetry with PowerShell
/// is not worth permanently handing C# code <c>Hashtable</c> and <c>object?</c>.
/// </para>
///
/// Nothing here is copied or decoded until asked for. <see cref="Http"/> is the escape hatch for
/// anything this does not cover — form files, the raw pipe, trailers.
/// </summary>
public sealed class CraftRequest
{
    private CraftPrincipal? _user;

    internal CraftRequest(HttpContext http, string route)
    {
        Http = http;
        Route = route;
    }

    /// <summary>The underlying context. Use it for anything this view does not expose.</summary>
    public HttpContext Http { get; }

    /// <summary>Route segment that matched, without the <c>/API/</c> prefix.</summary>
    public string Route { get; }

    public string Method => Http.Request.Method;

    /// <summary>Kestrel's own collection — not a copy.</summary>
    public IQueryCollection Query => Http.Request.Query;

    /// <summary>Kestrel's own collection — not a copy, and not lower-cased.</summary>
    public IHeaderDictionary Headers => Http.Request.Headers;

    public CancellationToken Aborted => Http.RequestAborted;

    /// <summary>
    /// The caller, decoded on first access.
    ///
    /// <para>
    /// By the time an endpoint runs, CraftAuthMiddleware has already normalised whatever the platform
    /// supplied — App Service EasyAuth claims, a reverse proxy's forward-auth headers, or a dev
    /// principal — into one <c>x-ms-client-principal</c> header. So this looks identical regardless of
    /// how the deployment authenticates, and an endpoint never has to care which it was.
    /// </para>
    ///
    /// Decoding is deferred because most endpoints never look.
    /// </summary>
    public CraftPrincipal User => _user ??= CraftPrincipal.FromHeaders(Http.Request.Headers);

    /// <summary>Deserializes the body. Returns default when the body is empty.</summary>
    public async ValueTask<T?> ReadJsonAsync<T>(
        JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        if (Http.Request.ContentLength == 0) return default;

        // Straight off the request pipe — no intermediate string, and no PSObject graph.
        return await JsonSerializer.DeserializeAsync<T>(Http.Request.Body, options, ct);
    }

    /// <summary>Reads the body as text, for endpoints that proxy it onward untouched.</summary>
    public async ValueTask<string> ReadBodyAsync(CancellationToken ct = default)
    {
        using var reader = new StreamReader(Http.Request.Body, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }
}

/// <summary>
/// The authenticated caller, decoded from the normalised Static Web Apps principal header.
/// </summary>
public sealed class CraftPrincipal
{
    private static readonly string[] s_none = [];

    private CraftPrincipal() { }

    public string UserId { get; private init; } = "";
    public string UserDetails { get; private init; } = "";
    public string IdentityProvider { get; private init; } = "";
    public IReadOnlyList<string> Roles { get; private init; } = s_none;

    /// <summary>True when no principal header was present — an anonymous request.</summary>
    public bool IsAnonymous => string.IsNullOrEmpty(UserDetails) && Roles.Count == 0;

    public bool IsInRole(string role) =>
        Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    internal static CraftPrincipal FromHeaders(IHeaderDictionary headers)
    {
        var encoded = headers["x-ms-client-principal"].ToString();
        if (string.IsNullOrEmpty(encoded)) return new CraftPrincipal();

        try
        {
            using var document = EasyAuthPrincipal.Decode(encoded);
            var root = document.RootElement;

            var roles = s_none;
            if (root.TryGetProperty("userRoles", out var rolesElement) &&
                rolesElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(rolesElement.GetArrayLength());
                foreach (var role in rolesElement.EnumerateArray())
                {
                    if (role.GetString() is { } value) list.Add(value);
                }
                roles = [.. list];
            }

            return new CraftPrincipal
            {
                UserId = Text(root, "userId"),
                UserDetails = Text(root, "userDetails"),
                IdentityProvider = Text(root, "identityProvider"),
                Roles = roles,
            };
        }
        catch
        {
            // An unparseable header is treated as anonymous, matching what the PowerShell path does
            // with one: pass it through rather than failing the request.
            return new CraftPrincipal();
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
}
