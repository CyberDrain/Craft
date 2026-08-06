using System.Reflection;
using Craft.Configuration;

namespace Craft.Endpoints;

/// <summary>One discovered native endpoint.</summary>
public sealed record NativeEndpointDescriptor(
    string Route,
    Type ImplementationType,
    CraftEndpointAttribute Metadata);

/// <summary>
/// Everything the configured assemblies contribute to the host: HTTP endpoints, at most one central
/// handler, scheduled tasks, and the successfully-scanned assemblies themselves (service modules are
/// discovered per assembly, so an assembly shipping only a handler or only tasks still gets its DI
/// registrations).
/// </summary>
public sealed record NativeEndpointCatalog(
    IReadOnlyList<NativeEndpointDescriptor> Endpoints,
    Type? HandlerType,
    IReadOnlyList<NativeTaskDescriptor> ScheduledTasks,
    IReadOnlyList<Assembly> Assemblies)
{
    /// <summary>The catalog of a deployment that configured nothing.</summary>
    public static NativeEndpointCatalog Empty { get; } = new([], null, [], []);

    public bool IsEmpty =>
        Endpoints.Count == 0 && HandlerType is null && ScheduledTasks.Count == 0;
}

/// <summary>
/// Finds native endpoints, the central handler and scheduled tasks in the assemblies an application
/// names, and decides what happens when an endpoint collides with a PowerShell route.
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
    /// Loads the configured assemblies and returns everything found in them.
    /// </summary>
    /// <param name="apiBasePath">Base directory relative paths in configuration resolve against.</param>
    public static NativeEndpointCatalog Discover(
        EndpointSettings settings, string apiBasePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        if (!settings.Enabled || settings.Assemblies.Count == 0) return NativeEndpointCatalog.Empty;

        var endpoints = new List<NativeEndpointDescriptor>();
        var handlerCandidates = new List<Type>();
        var taskCandidates = new List<Type>();
        var assemblies = new List<Assembly>();
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
                var endpointCount = 0;
                var taskCount = 0;

                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;

                    if (typeof(ICraftEndpoint).IsAssignableFrom(type))
                    {
                        var metadata = type.GetCustomAttribute<CraftEndpointAttribute>();
                        if (metadata is null)
                        {
                            logger.LogWarning(
                                "[Endpoints] {Type} implements ICraftEndpoint but has no [CraftEndpoint] " +
                                "attribute, so it has no route and was skipped.", type.FullName);
                            continue;
                        }

                        endpoints.Add(new NativeEndpointDescriptor(metadata.Route, type, metadata));
                        endpointCount++;
                    }

                    if (typeof(ICraftEndpointHandler).IsAssignableFrom(type))
                        handlerCandidates.Add(type);

                    if (typeof(ICraftScheduledTask).IsAssignableFrom(type))
                    {
                        taskCandidates.Add(type);
                        taskCount++;
                    }
                }

                assemblies.Add(assembly);
                logger.LogInformation(
                    "[Endpoints] {Assembly}: {Count} native endpoint(s), {Tasks} scheduled task(s)",
                    Path.GetFileName(path), endpointCount, taskCount);
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

        return new NativeEndpointCatalog(
            endpoints,
            SelectHandler(handlerCandidates),
            BuildScheduledTasks(taskCandidates, logger),
            assemblies);
    }

    /// <summary>
    /// Picks the application's central handler, or null when it ships none.
    /// </summary>
    /// <remarks>
    /// Two handlers is a startup failure, not a policy choice: which one authorized requests would
    /// come down to assembly scan order, and unlike a route collision there is no "shadowed" log
    /// line a reader could notice — the losing handler would simply never run.
    /// </remarks>
    internal static Type? SelectHandler(IReadOnlyList<Type> candidates)
    {
        if (candidates.Count > 1)
        {
            throw new InvalidOperationException(
                "Multiple ICraftEndpointHandler implementations found: " +
                string.Join(", ", candidates.Select(t => t.FullName)) +
                ". CRAFT dispatches every Central endpoint through ONE central handler; " +
                "which of these ran would depend on assembly scan order. Keep one.");
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Builds descriptors for the discovered scheduled tasks, skipping (with a log line) classes
    /// that implement the interface but never named their Command.
    /// </summary>
    internal static IReadOnlyList<NativeTaskDescriptor> BuildScheduledTasks(
        IReadOnlyList<Type> candidates, ILogger logger)
    {
        var tasks = new List<NativeTaskDescriptor>();
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in candidates)
        {
            var metadata = type.GetCustomAttribute<CraftScheduledTaskAttribute>();
            if (metadata is null)
            {
                logger.LogWarning(
                    "[Endpoints] {Type} implements ICraftScheduledTask but has no [CraftScheduledTask] " +
                    "attribute, so no timer Command can reach it and it was skipped.", type.FullName);
                continue;
            }

            if (!commands.Add(metadata.Command))
            {
                // Same reasoning as duplicate routes: which implementation a timer fired would be
                // assembly scan order, and there is no rollback story that explains that.
                throw new InvalidOperationException(
                    $"Duplicate native scheduled task Command '{metadata.Command}' " +
                    $"(declared again by {type.FullName}). Two [CraftScheduledTask] types answer " +
                    "to the same timer Command; rename one.");
            }

            tasks.Add(new NativeTaskDescriptor(metadata.Command, type, metadata));
        }

        return tasks;
    }

    /// <summary>
    /// Registers the catalog with the container: endpoint and task types at their declared
    /// lifetimes, the central handler, the scheduler's task lookup, and any application service
    /// modules found in the scanned assemblies.
    /// </summary>
    public static IServiceCollection AddNativeEndpoints(
        this IServiceCollection services,
        NativeEndpointCatalog catalog,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var endpoint in catalog.Endpoints)
        {
            services.Add(new ServiceDescriptor(
                endpoint.ImplementationType, endpoint.ImplementationType, endpoint.Metadata.Lifetime));
        }

        if (catalog.HandlerType is not null)
        {
            // Singleton like the endpoints it wraps: hold caches and pooled clients in fields.
            services.AddSingleton(typeof(ICraftEndpointHandler), catalog.HandlerType);
        }

        foreach (var task in catalog.ScheduledTasks)
        {
            services.Add(new ServiceDescriptor(
                task.ImplementationType, task.ImplementationType, task.Metadata.Lifetime));
        }

        // Replaces the Empty instance the host registered — last registration wins on resolve —
        // so SchedulerService sees the discovered set without a conditional in its constructor.
        if (catalog.ScheduledTasks.Count > 0)
            services.AddSingleton(new NativeScheduledTasks(catalog.ScheduledTasks));

        // An application hosted as a plugin has no Program.cs of its own, so this is its only
        // opportunity to register the services its endpoints and tasks inject. Scanned per assembly
        // rather than per endpoint: an assembly shipping only the handler or only tasks still counts.
        foreach (var assembly in catalog.Assemblies)
        {
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
