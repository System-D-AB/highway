using Highway.Client.Scanning;

namespace Highway.Client;

/// <summary>What kind of inbound capability a process provides (feature 024).</summary>
public enum CapabilityKind
{
    RpcService,
    QueueProcessor,
    Subscriber,

    /// <summary>A recurring-job schedule this process declares (feature 028).</summary>
    RecurringJob,
}

/// <summary>One hosted handler: the route, who implements it, and where that type came from.</summary>
public sealed record ProvidedCapability(
    CapabilityKind Kind,
    string Route,
    string ImplementationType,
    string SourceAssembly,

    /// <summary>The subscriber group, for <see cref="CapabilityKind.Subscriber"/> entries only.</summary>
    string? Group = null,

    /// <summary>Kind-specific detail — the schedule expression, for <see cref="CapabilityKind.RecurringJob"/>.</summary>
    string? Detail = null);

/// <summary>
/// Routes this process can address because it references their contracts. <b>Addressability,
/// not proof of calling</b> — a discovered contract says the process <i>can</i> reach the
/// route, nothing more. The manifest labels this half accordingly and so must anything built
/// on it.
/// </summary>
public sealed record CanUseContracts(
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Queues,
    IReadOnlyList<string> Channels);

/// <summary>
/// What this process provides and what it can use — the answer to "what does this deployment
/// actually do?", computed at startup instead of investigated at incident time (feature 024).
///
/// <para>Logged at engine start and exposed via <c>IHighwayEngine.Topology</c> so hosts and
/// tests assert on the structure, not on log text.</para>
/// </summary>
public sealed record TopologyManifest(
    string NodeName,
    IReadOnlyList<ProvidedCapability> Provides,
    CanUseContracts CanUse)
{
    internal static TopologyManifest Build(
        string nodeName, ICatalog catalog, ScanResult scan,
        IReadOnlyList<JobsOptions.Registration>? jobs = null)
    {
        List<ProvidedCapability> provides =
        [
            .. catalog.AllServices.Select(s => new ProvidedCapability(
                CapabilityKind.RpcService, s.Name,
                s.ImplementationType.Name, AssemblyOf(s.ImplementationType))),

            .. catalog.AllQueues.Select(q => new ProvidedCapability(
                CapabilityKind.QueueProcessor, q.Name,
                q.ProcessorType.Name, AssemblyOf(q.ProcessorType))),

            // One line per subscriber class, not per channel: "where did this handler come
            // from?" is the question the manifest answers, and a channel with three
            // subscribers has three answers. The group is the node name until feature 025.
            .. catalog.AllChannels.SelectMany(c => c.Subscribers.Select(sub =>
                new ProvidedCapability(
                    CapabilityKind.Subscriber, c.Name,
                    sub.ImplementationType.Name, AssemblyOf(sub.ImplementationType),
                    Group: nodeName))),

            // Declared schedules (028): route = the contract's queue. An unresolvable
            // contract is skipped here — engine start reports it properly, and a manifest
            // that throws would hide every other line behind one mistake.
            .. (jobs ?? []).Select(j => (Job: j, Queue: scan.QueueContracts.GetValueOrDefault(j.ContractType)))
                .Where(x => x.Queue is not null)
                .Select(x => new ProvidedCapability(
                    CapabilityKind.RecurringJob, x.Queue!,
                    x.Job.ContractType.Name, AssemblyOf(x.Job.ContractType),
                    Detail: $"{x.Job.JobName} {x.Job.Expression}")),
        ];

        provides.Sort((a, b) => (a.Kind, a.Route, a.ImplementationType)
            .CompareTo((b.Kind, b.Route, b.ImplementationType)));

        // CAN USE excludes what this process itself provides: the interesting half of
        // topology is what it reaches OUT to, and self-references would bury it.
        var provided = provides.Select(p => p.Route).ToHashSet(StringComparer.Ordinal);

        return new TopologyManifest(
            nodeName,
            provides,
            new CanUseContracts(
                Names(scan.RequestContracts.Values, provided),
                Names(scan.QueueContracts.Values, provided),
                Names(scan.MessageContracts.Values, provided)));
    }

    /// <summary>The D6 block format: one grep-able line per fact, stable order.</summary>
    internal string ToLogString()
    {
        var lines = new List<string> { $"Highway topology — node {NodeName}", "  PROVIDES" };

        if (Provides.Count == 0)
            lines.Add("    (nothing — this process hosts no handlers)");

        foreach (var p in Provides)
        {
            var kind = p.Kind switch
            {
                CapabilityKind.RpcService => "rpc  ",
                CapabilityKind.QueueProcessor => "queue",
                CapabilityKind.RecurringJob => "job  ",
                _ => "sub  ",
            };
            var group = p.Group is { } g ? $"  group={g}" : "";
            var detail = p.Detail is { } d ? $"  {d}" : "";
            lines.Add($"    {kind}  {p.Route}  {p.ImplementationType}  ({p.SourceAssembly}){group}{detail}");
        }

        lines.Add("  CAN USE (references the contract; not proof of calling)");

        if (CanUse.Services.Count == 0 && CanUse.Queues.Count == 0 && CanUse.Channels.Count == 0)
            lines.Add("    (nothing beyond what it provides)");

        lines.AddRange(CanUse.Services.Select(n => $"    rpc    {n}"));
        lines.AddRange(CanUse.Queues.Select(n => $"    queue  {n}"));
        lines.AddRange(CanUse.Channels.Select(n => $"    chan   {n}"));

        return string.Join(Environment.NewLine, lines);
    }

    private static string AssemblyOf(Type t) => t.Assembly.GetName().Name ?? "<unknown>";

    private static IReadOnlyList<string> Names(IEnumerable<string> routes, HashSet<string> provided)
        => [.. routes.Distinct(StringComparer.Ordinal).Where(r => !provided.Contains(r)).Order(StringComparer.Ordinal)];
}
