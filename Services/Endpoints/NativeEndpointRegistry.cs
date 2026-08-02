using System.Reflection;
using Craft.Configuration;

namespace Craft.Endpoints;

/// <summary>One discovered native endpoint.</summary>
public sealed record NativeEndpointDescriptor(
    string Route,
    Type ImplementationType,
    CraftEndpointAttribute Metadata);

/// <summary>
/// Finds native endpoints in the assemblies an application names, and decides what happens when one
/// collides with a PowerShell route.
///
/// <para>
/// Assemblies load into the DEFAULT load context deliberately, and there is no
/// unload. A collectible context would resolve its own copy of Craft.dll, producing two distinct
/// <c>ICraftEndpoint</c> types and a cast failure whose message is literally "unable to cast object
/// of type X to type X" — among the worst diagnostics in .NET. Unloading is unachievable in any case:
/// a collectible context stays alive while anything roots into it, and a static HttpClient, a cache
/// dictionary, a mapped route delegate and a DI singleton all do.
/// </para>
///
/// Routes are built once at startup, which matches how the PowerShell route table already works —
/// changing them means restarting the container.
/// </summary>
public static class NativeEndpointRegistry
{
    /// <summary>
    /// Loads the configured assemblies and returns every endpoint found in them.
    /// </summary>
    /// <param name="apiBasePath">Base directory relative paths in configuration resolve against.</param>
    public static IReadOnlyList<NativeEndpointDescriptor> Discover(
        EndpointSettings settings, string apiBasePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        if (!settings.Enabled || settings.Assemblies.Count == 0) return [];

        var found = new List<NativeEndpointDescriptor>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in settings.Assemblies)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            var path = Path.IsPathRooted(entry) ? entry : Path.Combine(apiBasePath, entry);
            if (!File.Exists(path))
            {
                logger.LogError("[Endpoints] Assembly not found: {Path}", path);
                continue;
            }

            // Dedupe by resolved path. An assembly named in both Endpoints:Assemblies and
            // Worker:SharedAssemblies loads once, which is what lets a half-migrated app share static
            // state between its PowerShell and native paths.
            if (!seenPaths.Add(Path.GetFullPath(path))) continue;

            try
            {
                var assembly = Assembly.LoadFrom(path);
                var count = 0;

                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!typeof(ICraftEndpoint).IsAssignableFrom(type)) continue;

                    var metadata = type.GetCustomAttribute<CraftEndpointAttribute>();
                    if (metadata is null)
                    {
                        logger.LogWarning(
                            "[Endpoints] {Type} implements ICraftEndpoint but has no [CraftEndpoint] " +
                            "attribute, so it has no route and was skipped.", type.FullName);
                        continue;
                    }

                    found.Add(new NativeEndpointDescriptor(metadata.Route, type, metadata));
                    count++;
                }

                logger.LogInformation("[Endpoints] {Assembly}: {Count} native endpoint(s)",
                    Path.GetFileName(path), count);
            }
            catch (ReflectionTypeLoadException ex)
            {
                // The characteristic failure when the app was built against a different version of
                // something CRAFT already ships. Name the actual loader errors — the outer message
                // alone ("Unable to load one or more of the requested types") says nothing useful.
                logger.LogError(ex, "[Endpoints] Failed to load types from {Path}: {Errors}",
                    path, string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Endpoints] Failed to load {Path}", path);
            }
        }

        return found;
    }

    /// <summary>
    /// Registers discovered endpoints (and any application service modules in the same assemblies)
    /// with the container.
    /// </summary>
    public static IServiceCollection AddNativeEndpoints(
        this IServiceCollection services,
        IReadOnlyList<NativeEndpointDescriptor> endpoints,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoints);

        var modulesDone = new HashSet<Assembly>();

        foreach (var endpoint in endpoints)
        {
            services.Add(new ServiceDescriptor(
                endpoint.ImplementationType, endpoint.ImplementationType, endpoint.Metadata.Lifetime));

            // An application hosted as a plugin has no Program.cs of its own, so this is its only
            // opportunity to register the services its endpoints inject.
            var assembly = endpoint.ImplementationType.Assembly;
            if (!modulesDone.Add(assembly)) continue;

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(ICraftServiceModule).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is ICraftServiceModule module)
                    module.ConfigureServices(services, configuration);
            }
        }

        return services;
    }

    /// <summary>
    /// Applies the configured collision policy and returns the endpoints that should actually be
    /// mapped.
    /// </summary>
    /// <remarks>
    /// A collision is not an error in itself — it is how an endpoint gets migrated, with the
    /// PowerShell function left in place as the rollback. What must not happen is for it to be
    /// silent, so every shadowed route is logged whichever way the policy resolves it.
    /// </remarks>
    public static IReadOnlyList<NativeEndpointDescriptor> ResolveCollisions(
        IReadOnlyList<NativeEndpointDescriptor> native,
        IReadOnlyCollection<string> powerShellRoutes,
        string policy,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(powerShellRoutes);
        ArgumentNullException.ThrowIfNull(logger);

        var psRoutes = new HashSet<string>(powerShellRoutes, StringComparer.OrdinalIgnoreCase);
        var collisions = native.Where(e => psRoutes.Contains(e.Route)).ToList();

        // Two native endpoints on one route is always a bug — there is no rollback story that
        // explains it, and whichever won would be down to assembly scan order.
        var duplicates = native.GroupBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Duplicate native endpoint route(s): " + string.Join(", ", duplicates) +
                ". Two [CraftEndpoint] types declare the same route; which one won would depend on " +
                "assembly scan order.");
        }

        if (collisions.Count == 0) return native;

        var routes = string.Join(", ", collisions.Select(c => c.Route));

        if (policy.Equals("Fail", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Native endpoint(s) collide with PowerShell routes: {routes}. " +
                "App:Endpoints:OnCollision is 'Fail'. Remove the PowerShell function, rename the " +
                "route, or set the policy to PreferNative or PreferPowerShell.");
        }

        if (policy.Equals("PreferPowerShell", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "[Endpoints] {Count} native endpoint(s) SHADOWED by PowerShell (OnCollision=" +
                "PreferPowerShell): {Routes}", collisions.Count, routes);
            return native.Where(e => !psRoutes.Contains(e.Route)).ToList();
        }

        logger.LogWarning(
            "[Endpoints] {Count} PowerShell route(s) shadowed by native endpoints: {Routes}. " +
            "The PowerShell functions are still loaded and are reachable again by setting " +
            "App:Endpoints:OnCollision=PreferPowerShell.", collisions.Count, routes);
        return native;
    }
}
