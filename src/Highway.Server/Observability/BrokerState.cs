using Highway.Server.Internal;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Highway.Server.Observability;

/// <summary>One queue's live state, as an operator would want to read it.</summary>
internal sealed record QueueStateDto(
    string Name,
    long Depth,
    long Bytes,
    long MaxBytes,
    long InFlight,
    long DeadLettered,
    long Delayed,
    bool IsSubscriberGroup);

/// <summary>
/// The result of a state read. **Never an exception**: a view that cannot load must say so while
/// the rest of the page keeps working (020 R1.3), which is C7.1 applied to the dashboard itself.
/// </summary>
internal sealed record StateResult<T>(T? Value, string? Unavailable)
{
    public static StateResult<T> Ok(T value) => new(value, null);
    public static StateResult<T> Fail(string why) => new(default, why);
}

/// <summary>
/// Read-only access to broker state, for components hosted inside the server process.
///
/// <para><b>Read-only by construction, not by convention</b> (020 R1.4). The interface exposes
/// no mutation, so "the dashboard cannot change anything" is a compile-time fact rather than a
/// code-review promise.</para>
/// </summary>
internal interface IBrokerState
{
    Task<StateResult<IReadOnlyList<QueueStateDto>>> QueuesAsync(CancellationToken ct = default);

    /// <summary>Registered nodes and what each declared (022 T2).</summary>
    Task<StateResult<IReadOnlyList<NodeDto>>> NodesAsync(CancellationToken ct = default);

    /// <summary>
    /// The catalogue: every entity, classified, as a union of what nodes declared and what the
    /// recorder observed. <paramref name="observedNames"/> comes from the in-process recorder,
    /// which is why an entity nobody declared can still appear.
    /// </summary>
    Task<StateResult<IReadOnlyList<CatalogueEntryDto>>> CatalogueAsync(
        IReadOnlyCollection<string> observedNames, CancellationToken ct = default);
}

/// <summary>
/// Reads broker state over a connection the <b>server</b> builds from its own options
/// (020 decision 1, fallback C).
///
/// <para><b>Why not in-process.</b> Garnet exposes no supported API for reading keys outside a
/// transaction; the embedded-server pattern that would allow it lives in Garnet's benchmark
/// project as ~400 lines of <c>internal</c> scaffolding. Vendoring that would let any Garnet
/// upgrade break the dashboard's read path — more coupling than a read-only view is worth.</para>
///
/// <para><b>Why this is not 018's bug again.</b> 018's failure was that the configuration had to
/// be <i>mirrored</i> and a setting was missed. This builds from the same options object the
/// server was configured with, through <see cref="LoopbackConnection"/>, which is the only place
/// that knows how.</para>
///
/// <para><b>Connected lazily, never at startup.</b> A broker must not fail to start because a
/// diagnostic component could not connect to it.</para>
/// </summary>
internal sealed class BrokerState : IBrokerState, IAsyncDisposable
{
    private readonly HighwayServerOptions _opts;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private ConnectionMultiplexer? _mux;

    public BrokerState(HighwayServerOptions opts, ILogger logger)
    {
        _opts = opts;
        _logger = logger;
    }

    public async Task<StateResult<IReadOnlyList<QueueStateDto>>> QueuesAsync(CancellationToken ct = default)
    {
        var db = await TryConnectAsync(ct).ConfigureAwait(false);
        if (db is null)
            return StateResult<IReadOnlyList<QueueStateDto>>.Fail(UnavailableReason());

        try
        {
            var server = _mux!.GetServers()[0];
            var queues = new List<QueueStateDto>();

            // The queue list is discovered from the live keys rather than a registry, because a
            // queue exists the moment something is sent to it — there is no declaration step.
            foreach (var key in server.Keys(pattern: "hw:q:*:q", pageSize: 250))
            {
                var name = QueueNameFromKey(key.ToString());
                if (name is null) continue;

                queues.Add(new QueueStateDto(
                    Name: name,
                    Depth: await db.ListLengthAsync(HighwayKeys.Queue(name)).ConfigureAwait(false),
                    Bytes: await ReadCounterAsync(db, HighwayKeys.QueueBytes(name)).ConfigureAwait(false),
                    MaxBytes: _opts.MaxQueueBytes,
                    InFlight: await CountInFlightAsync(db, server, name).ConfigureAwait(false),
                    DeadLettered: await db.ListLengthAsync(HighwayKeys.QueueDeadLetter(name)).ConfigureAwait(false),
                    Delayed: await db.SortedSetLengthAsync(HighwayKeys.QueueDelayed(name)).ConfigureAwait(false),

                    // 018 made a subscriber group a queue, so the only thing distinguishing one
                    // is its derived name. That is the whole reason one view covers both verbs.
                    IsSubscriberGroup: name.Contains('@')));
            }

            return StateResult<IReadOnlyList<QueueStateDto>>.Ok(queues);
        }
        catch (Exception ex)
        {
            // Swallowed and reported as unavailable. A dashboard that can take down its broker
            // is worse than a dashboard that shows one empty panel.
            _logger.LogDebug(ex, "Reading queue state failed");
            return StateResult<IReadOnlyList<QueueStateDto>>.Fail($"could not read queue state: {ex.Message}");
        }
    }

    public async Task<StateResult<IReadOnlyList<NodeDto>>> NodesAsync(CancellationToken ct = default)
    {
        var db = await TryConnectAsync(ct).ConfigureAwait(false);
        if (db is null)
            return StateResult<IReadOnlyList<NodeDto>>.Fail(UnavailableReason());

        try
        {
            return StateResult<IReadOnlyList<NodeDto>>.Ok(await ReadNodesAsync(db).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading nodes failed");
            return StateResult<IReadOnlyList<NodeDto>>.Fail($"could not read nodes: {ex.Message}");
        }
    }

    public async Task<StateResult<IReadOnlyList<CatalogueEntryDto>>> CatalogueAsync(
        IReadOnlyCollection<string> observedNames, CancellationToken ct = default)
    {
        var db = await TryConnectAsync(ct).ConfigureAwait(false);

        // Under mTLS the declared half is unreachable, so the catalogue degrades to what the
        // recorder observed rather than disappearing (022 review R-3A). Everything is then
        // NeverDeclared, which is honest: nothing could be confirmed as declared.
        var nodes = db is null ? [] : await ReadNodesAsync(db).ConfigureAwait(false);

        var services = nodes.SelectMany(n => n.Services).ToHashSet(StringComparer.Ordinal);
        var queues = nodes.SelectMany(n => n.Queues).ToHashSet(StringComparer.Ordinal);
        var channels = nodes.SelectMany(n => n.Channels).ToHashSet(StringComparer.Ordinal);
        var nodeNames = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

        // Union of THREE sources, not two. A group queue exists as a structure the moment a
        // publish fans out, but nothing declares it (its name is derived) and the recorder does
        // not see it until a subscriber claims from it. Without the structures, a channel's
        // groups are invisible until they are consumed — which is exactly when an operator most
        // wants to see them sitting there unconsumed.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in services) names.Add(n);
        foreach (var n in queues) names.Add(n);
        foreach (var n in channels) names.Add(n);
        foreach (var n in observedNames) names.Add(n);

        if (db is not null)
        {
            foreach (var q in await ReadQueueNamesAsync(db).ConfigureAwait(false))
                names.Add(q);
        }

        var entries = new List<CatalogueEntryDto>(names.Count);
        foreach (var name in names)
        {
            var (kind, parent) = Catalogue.Classify(name, services, queues, channels, nodeNames);

            var hosts = nodes
                .Where(n => Declares(n, name, kind, parent))
                .Select(n => n.Name)
                .ToArray();

            entries.Add(new CatalogueEntryDto(name, kind, StateOf(db, nodes, hosts), parent, hosts));
        }

        return StateResult<IReadOnlyList<CatalogueEntryDto>>.Ok(entries);
    }

    /// <summary>
    /// Whether a node declared this entity. A group is declared by whoever declared its channel —
    /// the group name is derived, so no node ever names it directly (018).
    /// </summary>
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

    private static EntityState StateOf(IDatabase? db, IReadOnlyList<NodeDto> nodes, IReadOnlyList<string> hosts)
    {
        if (db is null) return EntityState.Unknown;      // registry unreadable (mTLS)
        if (hosts.Count == 0) return EntityState.NeverDeclared;

        return nodes.Any(n => hosts.Contains(n.Name) && n.IsLive)
            ? EntityState.Live
            : EntityState.HostStale;
    }

    /// <summary>Queue names that exist as structures, whether or not anyone declared them.</summary>
    private async Task<IReadOnlyList<string>> ReadQueueNamesAsync(IDatabase db)
    {
        await Task.Yield();

        var server = _mux!.GetServers()[0];
        var names = new List<string>();

        foreach (var key in server.Keys(pattern: "hw:q:*:q", pageSize: 250))
        {
            if (QueueNameFromKey(key.ToString()) is { } name)
                names.Add(name);
        }

        return names;
    }

    private async Task<IReadOnlyList<NodeDto>> ReadNodesAsync(IDatabase db)
    {
        var raw = await db.StringGetAsync(HighwayKeys.RegistrationNodeList).ConfigureAwait(false);
        if (!raw.HasValue) return [];

        var nodes = new List<NodeDto>();
        foreach (var id in raw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = await db.StringGetAsync(HighwayKeys.RegistrationNode(id)).ConfigureAwait(false);
            if (!record.HasValue) continue;

            nodes.Add(Catalogue.ReadNode(id, (byte[])record!, _opts.NodeExpiry));
        }

        return nodes;
    }

    /// <summary>Extracts <c>{name}</c> from <c>hw:q:{name}:q</c>, tolerating names containing colons.</summary>
    private static string? QueueNameFromKey(string key)
    {
        const string prefix = "hw:q:";
        const string suffix = ":q";

        return key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith(suffix, StringComparison.Ordinal)
            ? key[prefix.Length..^suffix.Length]
            : null;
    }

    private static async Task<long> ReadCounterAsync(IDatabase db, string key)
    {
        var raw = await db.StringGetAsync(key).ConfigureAwait(false);
        return raw.HasValue && long.TryParse(raw.ToString(), out var value) ? value : 0;
    }

    /// <summary>
    /// Claimed-but-unacknowledged work, summed across every worker's processing list. There is
    /// one per node, so this is a scan rather than a single read.
    /// </summary>
    private static async Task<long> CountInFlightAsync(IDatabase db, IServer server, string queue)
    {
        long total = 0;
        foreach (var key in server.Keys(pattern: $"hw:q:{queue}:proc:*", pageSize: 100))
            total += await db.ListLengthAsync(key).ConfigureAwait(false);

        return total;
    }

    private async Task<IDatabase?> TryConnectAsync(CancellationToken ct)
    {
        if (_mux is { IsConnected: true })
            return _mux.GetDatabase();

        if (LoopbackConnection.Unsupported(_opts) is not null)
            return null;

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_mux is { IsConnected: true })
                return _mux.GetDatabase();

            _mux?.Dispose();
            _mux = await ConnectionMultiplexer
                .ConnectAsync(LoopbackConnection.Configure(_opts))
                .ConfigureAwait(false);

            return _mux.IsConnected ? _mux.GetDatabase() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Broker state connection failed");
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private string UnavailableReason()
        => LoopbackConnection.Unsupported(_opts)
           ?? "could not connect to the broker to read its state";

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null)
            await _mux.DisposeAsync().ConfigureAwait(false);

        _connectLock.Dispose();
    }
}
