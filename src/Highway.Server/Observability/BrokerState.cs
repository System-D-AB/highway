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
