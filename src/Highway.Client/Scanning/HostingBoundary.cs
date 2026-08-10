using System.Reflection;
using Highway.Abstractions;

namespace Highway.Client.Scanning;

/// <summary>
/// Selects which scanned assemblies may contribute handlers, per <see cref="HostingMode"/>
/// (feature 024), and computes the report the engine speaks from at startup.
/// </summary>
internal static class HostingBoundary
{
    /// <summary>
    /// The handler-assembly set. Contract scanning always uses the full
    /// <paramref name="scanned"/> closure; this narrows only who gets to host.
    /// </summary>
    public static IReadOnlyList<Assembly> SelectHandlerAssemblies(
        HostingMode mode,
        IReadOnlyList<Assembly> scanned,
        Assembly? entryAssembly,
        IReadOnlyCollection<Assembly> declared)
    {
        if (mode == HostingMode.Implicit)
            return scanned;

        return scanned
            .Where(a => IsDeclared(a, declared)
                || (mode == HostingMode.Declared && a == entryAssembly))
            .ToList();
    }

    public static bool IsDeclared(Assembly assembly, IReadOnlyCollection<Assembly> declared)
        => declared.Contains(assembly)
           || assembly.IsDefined(typeof(HighwayHostModuleAttribute), inherit: false);

    /// <summary>
    /// The Implicit-mode half of the startup report: assemblies that contributed handlers
    /// without anyone's consent — neither the entry assembly nor a declared module. Hosted
    /// (that is the mode's contract), but announced, because "reference equals hosting" is
    /// the accident three architecture reviews converged on.
    /// </summary>
    public static IReadOnlyList<SkippedHandlerAssembly> ReferenceHostedAssemblies(
        ScanResult result,
        Assembly? entryAssembly,
        IReadOnlyCollection<Assembly> declared)
    {
        IEnumerable<(Assembly Assembly, Type Type)> handlers =
        [
            .. result.Services.Select(s => (s.ImplementationType.Assembly, s.ImplementationType)),
            .. result.Queues.Select(q => (q.ProcessorType.Assembly, q.ProcessorType)),
            .. result.Channels.SelectMany(c => c.Subscribers)
                .Select(s => (s.ImplementationType.Assembly, s.ImplementationType)),
        ];

        return handlers
            .Where(h => h.Assembly != entryAssembly && !IsDeclared(h.Assembly, declared))
            .GroupBy(h => h.Assembly)
            .Select(g => new SkippedHandlerAssembly(
                g.Key.GetName().Name ?? g.Key.FullName ?? "<unknown>",
                [.. g.Select(h => h.Type.FullName ?? h.Type.Name).Distinct().Order()]))
            .OrderBy(a => a.AssemblyName, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// What the hosting boundary decided, for the engine to say out loud at startup
/// (feature 024). Exactly one of the two lists is ever populated: skipped assemblies exist
/// only in <c>Declared</c>/<c>ExplicitOnly</c>, reference-hosted ones only in <c>Implicit</c>.
/// </summary>
internal sealed record HostingReport(
    HostingMode Mode,
    IReadOnlyList<SkippedHandlerAssembly> Skipped,
    IReadOnlyList<SkippedHandlerAssembly> ReferenceHosted);
