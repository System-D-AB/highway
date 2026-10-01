using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Rocks;

namespace Highway.Server.Resp;

/// <summary>
/// The in-process, store-backed reimplementation of the broker-state reads the embedded test
/// server's diagnostic hooks need (040 T8). It answers the same questions <c>BrokerState</c> did
/// — queue depths, registered nodes, the classified catalogue — but reads the
/// <see cref="IHighwayStore"/> directly instead of issuing raw Redis commands the RESP server does
/// not serve. Same DTOs, so <c>HighwayTestServer</c>'s hooks and the tests that call them are
/// unchanged.
/// </summary>
internal sealed class StoreBrokerState(IHighwayStore store, HighwayServerOptions options, ObservedAddressRegistry? observed = null) : IBrokerState
{
    public IReadOnlyList<(string Name, long Depth, long Bytes)> Queues()
        => [.. EachQueue().Select(q => (q.Name, q.Box.Ready, q.Box.Bytes))];

    /// <summary>Every queue box with its counters open (065) — no data is scanned.</summary>
    private IEnumerable<(string Name, Storage.Boxes.WorkBox Box)> EachQueue()
    {
        var rt = new Storage.Boxes.BoxRuntime();
        using var batch = store.NewBatch();
        foreach (var (_, name, id) in Storage.Boxes.Catalog.List(store, batch, Storage.Boxes.BoxKeys.Kind.Queue))
        {
            using var wb = new Storage.Boxes.WorkBox(store, rt, id, tracksBytes: true, batch);
            yield return (name, wb);
        }
    }

    public Task<StateResult<IReadOnlyList<QueueStateDto>>> QueuesAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = new List<QueueStateDto>();
            foreach (var (name, wb) in EachQueue())
            {
                rows.Add(new QueueStateDto(
                    Name: name,
                    Depth: wb.Ready,
                    Bytes: wb.Bytes,
                    MaxBytes: options.MaxQueueBytes,
                    InFlight: wb.InFlight,
                    DeadLettered: wb.Dead,
                    Delayed: wb.Delayed,
                    IsSubscriberGroup: name.Contains('@'),
                    Draining: wb.DrainingSince != 0));
            }
            return Task.FromResult(StateResult<IReadOnlyList<QueueStateDto>>.Ok(rows));
        }
        catch (Exception ex)
        {
            return Task.FromResult(StateResult<IReadOnlyList<QueueStateDto>>.Fail($"could not read queue state: {ex.Message}"));
        }
    }

    public Task<StateResult<IReadOnlyList<NodeDto>>> NodesAsync(CancellationToken ct = default)
    {
        try { return Task.FromResult(StateResult<IReadOnlyList<NodeDto>>.Ok(Nodes())); }
        catch (Exception ex) { return Task.FromResult(StateResult<IReadOnlyList<NodeDto>>.Fail($"could not read nodes: {ex.Message}")); }
    }

    public Task<StateResult<IReadOnlyList<JobScheduleDto>>> JobsAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = new List<JobScheduleDto>();
            foreach (var (queue, wb) in EachQueue())
            {
                foreach (var job in wb.Schedules())
                {
                    rows.Add(new JobScheduleDto(
                        queue, job.Job, job.Expression,
                        new DateTimeOffset(job.NextFire, TimeSpan.Zero),
                        job.LastFire > 0 ? new DateTimeOffset(job.LastFire, TimeSpan.Zero) : null));
                }
            }
            return Task.FromResult(StateResult<IReadOnlyList<JobScheduleDto>>.Ok(rows));
        }
        catch (Exception ex)
        {
            return Task.FromResult(StateResult<IReadOnlyList<JobScheduleDto>>.Fail($"could not read schedules: {ex.Message}"));
        }
    }

    public Task<StateResult<IReadOnlyList<CatalogueEntryDto>>> CatalogueAsync(
        IReadOnlyCollection<string> observedNames, CancellationToken ct = default)
    {
        try { return Task.FromResult(StateResult<IReadOnlyList<CatalogueEntryDto>>.Ok(Catalogue(observedNames))); }
        catch (Exception ex) { return Task.FromResult(StateResult<IReadOnlyList<CatalogueEntryDto>>.Fail($"could not read catalogue: {ex.Message}")); }
    }

    public Task<StateResult<IReadOnlyDictionary<string, string>>> ReplicationAsync(CancellationToken ct = default)
    {
        try
        {
            if (store is not RocksDbStore rocks)
                return Task.FromResult(StateResult<IReadOnlyDictionary<string, string>>.Fail("replication requires a durable RocksDB broker"));

            var map = rocks.Replication.StatsFields().ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
            // 049/050 T5: the dashboard succession view reads roster.* — include the replicated roster
            // (the same fields HW.REPL.STATUS emits) so it populates from api/replication too, not only
            // from HW.REPL.STATUS. Without this the succession table was always empty on the dashboard.
            var roster = Storage.Rocks.RosterStore.Read(store);
            map["roster.version"] = roster.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var r = 0;
            foreach (var m in roster.Members)
            {
                map[$"roster.{r}.id"] = m.NodeId;
                map[$"roster.{r}.priority"] = m.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
                map[$"roster.{r}.endpoint"] = m.Endpoint;
                r++;
            }
            return Task.FromResult(StateResult<IReadOnlyDictionary<string, string>>.Ok((IReadOnlyDictionary<string, string>)map));
        }
        catch (Exception ex)
        {
            return Task.FromResult(StateResult<IReadOnlyDictionary<string, string>>.Fail($"could not read replication status: {ex.Message}"));
        }
    }

    public IReadOnlyList<NodeDto> Nodes()
    {
        using var snap = store.Snapshot();
        var nodes = new List<NodeDto>();
        foreach (var id in StoreInspection.NodeIds(store, snap))
        {
            var record = StoreInspection.RegistrationRecord(store, snap, id);
            if (record is null) continue;
            var seen = Commands.Ported.RegistrySupport.SeenTicks(store, snap, id, record);   // 060
            var dto = Observability.Catalogue.ReadNode(id, record, options.NodeExpiry, seen);
            // 048: attach the observed peer address (from CLIENT SETNAME), or leave null → "not connected".
            nodes.Add(observed is null ? dto : dto with { SeenFrom = observed.AddressOf(dto.Name) });
        }
        return nodes;
    }

    public IReadOnlyList<CatalogueEntryDto> Catalogue(IReadOnlyCollection<string> observedNames)
    {
        using var snap = store.Snapshot();
        var nodes = Nodes();

        var services = nodes.SelectMany(n => n.Services).ToHashSet(StringComparer.Ordinal);
        var queues = nodes.SelectMany(n => n.Queues).ToHashSet(StringComparer.Ordinal);
        var channels = nodes.SelectMany(n => n.Channels).ToHashSet(StringComparer.Ordinal);
        var nodeNames = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in services) names.Add(n);
        foreach (var n in queues) names.Add(n);
        foreach (var n in channels) names.Add(n);
        foreach (var n in observedNames) names.Add(n);
        foreach (var q in StoreInspection.QueueNames(store)) names.Add(q);

        var entries = new List<CatalogueEntryDto>(names.Count);
        foreach (var name in names)
        {
            var (kind, parent) = Observability.Catalogue.Classify(name, services, queues, channels, nodeNames);

            var hosts = nodes.Where(n => Declares(n, name, kind, parent)).Select(n => n.Name).ToArray();

            if (kind == EntityKind.Group && parent is not null)
            {
                var members = StoreInspection.GroupMembers(store, snap, parent, name[(parent.Length + 1)..]);
                if (members.Length > 0) hosts = members;
            }

            entries.Add(new CatalogueEntryDto(name, kind, StateOf(nodes, hosts), parent, hosts));
        }
        return entries;
    }

    private static bool Declares(NodeDto node, string name, EntityKind kind, string? parent)
        => kind switch
        {
            EntityKind.Service => node.Services.Contains(name),
            EntityKind.Queue => node.Queues.Contains(name),
            EntityKind.Channel => node.Channels.Contains(name),
            EntityKind.Group => parent is not null && node.Channels.Contains(parent),
            EntityKind.Node => node.Name == name,
            _ => false,
        };

    private static EntityState StateOf(IReadOnlyList<NodeDto> nodes, IReadOnlyList<string> hosts)
    {
        if (hosts.Count == 0) return EntityState.NeverDeclared;
        return nodes.Any(n => hosts.Contains(n.Name) && n.IsLive) ? EntityState.Live : EntityState.HostStale;
    }
}
