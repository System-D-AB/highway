using System.Reflection;

namespace Highway.Client.Scanning;

/// <summary>
/// Finds the assemblies worth scanning: those that reference
/// <c>Highway.Abstractions</c>, and therefore might contain services,
/// subscribers, or contracts.
///
/// <para><b>Why this walks the reference closure.</b> An earlier version filtered
/// <see cref="AppDomain.CurrentDomain"/>'s loaded assemblies alone. The .NET
/// runtime loads assemblies lazily — on first use, not at startup — so a
/// contracts assembly that nothing has touched yet is simply absent from that
/// list. The failure this produced was badly misleading: a caller that
/// referenced a contracts library but hosted no services of its own would find
/// none of its own contracts, and <c>ExecuteAsync</c> returned
/// <c>SERVICE_NOT_FOUND</c> for a service running perfectly well in another
/// process. It also depended on load order, so it reproduced for callers and not
/// for hosts.</para>
///
/// <para>Discovery therefore seeds from the loaded set <em>and</em> walks the
/// entry assembly's references transitively, loading what it finds. That makes
/// the result depend on what the application references rather than on what the
/// runtime happens to have needed so far — which is what product goal G3
/// ("assembly scanning discovers everything at startup, no manual registration")
/// actually promises.</para>
///
/// <para><see cref="HighwayOptions.AdditionalAssemblies"/> remains for genuinely
/// dynamic cases — plugins and assemblies loaded at runtime, which no static
/// reference walk can predict.</para>
/// </summary>
internal sealed class DefaultAssemblySource(HighwayOptions options) : IAssemblySource
{
    private static readonly AssemblyName HighwayAbstractionsName =
        typeof(Highway.Abstractions.IReturn<>).Assembly.GetName();

    public IReadOnlyList<Assembly> GetAssemblies()
    {
        var candidates = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        // Seed with everything already loaded.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && assembly.FullName is { } name)
                candidates[name] = assembly;
        }

        // Then walk what the application references but has not needed yet.
        if (Assembly.GetEntryAssembly() is { } entry)
            AddReferenceClosure(entry, candidates);

        var assemblies = candidates.Values
            .Where(a => !a.IsDynamic)
            .Where(a => !IsExcluded(a))
            .Where(ReferencesHighway)
            .ToList();

        foreach (var additional in options.AdditionalAssemblies)
        {
            if (!assemblies.Contains(additional))
                assemblies.Add(additional);
        }

        return assemblies;
    }

    /// <summary>
    /// Breadth-first walk of <paramref name="root"/>'s references, loading each
    /// one so its own references can be inspected in turn.
    /// </summary>
    private static void AddReferenceClosure(Assembly root, Dictionary<string, Assembly> into)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.FullName is not { } currentName || !visited.Add(currentName))
                continue;

            into[currentName] = current;

            foreach (var reference in current.GetReferencedAssemblies())
            {
                // Framework assemblies cannot contain Highway contracts, and
                // walking into them would load a large graph for nothing.
                if (IsFrameworkAssembly(reference) || visited.Contains(reference.FullName))
                    continue;

                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (Exception ex) when (ex is FileNotFoundException
                                              or FileLoadException
                                              or BadImageFormatException)
                {
                    // A reference that cannot be resolved is not fatal: it simply
                    // contributes nothing to scan. Trimmed and plugin-style
                    // deployments hit this legitimately.
                }
            }
        }
    }

    /// <summary>
    /// True for assemblies that cannot contain Highway types. A prefix test
    /// rather than a load-and-inspect, because the point is to avoid loading
    /// the framework graph at startup.
    /// </summary>
    private static bool IsFrameworkAssembly(AssemblyName name)
        => name.Name is not { } n
           || n.StartsWith("System", StringComparison.Ordinal)
           || n.StartsWith("Microsoft.", StringComparison.Ordinal)
           || n.StartsWith("netstandard", StringComparison.Ordinal)
           || n.StartsWith("mscorlib", StringComparison.Ordinal)
           || n.StartsWith("WindowsBase", StringComparison.Ordinal);

    private bool IsExcluded(Assembly assembly)
        => options.ExcludedAssemblies.Any(predicate => predicate(assembly));

    private static bool ReferencesHighway(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (name is "Highway.Abstractions" or "Highway.Client" or "Highway.Server")
            return true;

        return assembly.GetReferencedAssemblies()
            .Any(r => AssemblyName.ReferenceMatchesDefinition(r, HighwayAbstractionsName));
    }
}
