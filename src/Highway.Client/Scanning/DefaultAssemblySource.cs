using System.Reflection;

namespace Highway.Client.Scanning;

/// <summary>
/// Default assembly source that scans AppDomain for assemblies referencing Highway.Abstractions.
/// </summary>
internal sealed class DefaultAssemblySource(HighwayOptions options) : IAssemblySource
{
    private static readonly AssemblyName HighwayAbstractionsName = typeof(Highway.Abstractions.IReturn<>).Assembly.GetName();

    public IReadOnlyList<Assembly> GetAssemblies()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Where(a => !IsExcluded(a))
            .Where(a => ReferencesHighway(a))
            .ToList();

        // Add explicitly specified assemblies
        foreach (var additional in options.AdditionalAssemblies)
        {
            if (!assemblies.Contains(additional))
                assemblies.Add(additional);
        }

        return assemblies;
    }

    private bool IsExcluded(Assembly assembly)
    {
        return options.ExcludedAssemblies.Any(predicate => predicate(assembly));
    }

    private static bool ReferencesHighway(Assembly assembly)
    {
        // Include Highway's own assemblies
        var name = assembly.GetName().Name;
        if (name is "Highway.Abstractions" or "Highway.Client" or "Highway.Server")
            return true;

        return assembly.GetReferencedAssemblies()
            .Any(r => AssemblyName.ReferenceMatchesDefinition(r, HighwayAbstractionsName));
    }
}
