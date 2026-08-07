using Highway.Client.Wire;
using StackExchange.Redis;

namespace Highway.Client.Engine;

/// <summary>
/// The wire surface of <see cref="HighwayConnection"/> — every HW.* command
/// shape, the reply-slot operations, and doorbell subscription. Loops, the
/// registry, and the client depend on this interface (mockable in unit tests);
/// the command names and argument orders themselves live in exactly one place:
/// <see cref="HighwayConnection"/>.
/// </summary>
/// <summary>
/// Outcome of the <c>HW.HEARTBEAT</c> liveness form (feature 006).
/// </summary>
internal enum HeartbeatReply
{
    /// <summary>Liveness refreshed.</summary>
    Ok,

    /// <summary>
    /// The server holds no registration for this node — it was never registered,
    /// was pruned, or the registry was lost with the server. The node must send
    /// the registration form before it can be discovered again.
    /// </summary>
    ReRegisterRequired,
}

internal interface IHighwayConnection
{
    Task CallAsync(string service, string requestId, byte[] envelope, CancellationToken ct = default);
    Task<(string RequestId, byte[] Payload)?> DequeueAsync(string service, string nodeId, CancellationToken ct = default);
    Task ReplyAsync(string requestId, byte[] envelope, CancellationToken ct = default);
    Task AckAsync(string service, string nodeId, string requestId, CancellationToken ct = default);
    Task<long> PublishCommandAsync(string channel, byte[] envelope, CancellationToken ct = default);
    Task SubscribeGroupAsync(string channel, string group, CancellationToken ct = default);
    Task<IReadOnlyList<(long MessageId, byte[] Payload)>> ReceiveAsync(string channel, string group, int count, CancellationToken ct = default);
    Task RackAsync(string channel, string group, long messageId, CancellationToken ct = default);
    Task<byte[]?> GetReplySlotAsync(string requestId, CancellationToken ct = default);
    Task DeleteReplySlotAsync(string requestId, CancellationToken ct = default);
    Task SubscribeDoorbellAsync(string channel, Action<string> onMessage, CancellationToken ct = default);

    // Registry (feature 006)
    Task RegisterAsync(string nodeId, byte[] catalogJson, CancellationToken ct = default);
    Task<HeartbeatReply> HeartbeatAsync(string nodeId, CancellationToken ct = default);
    Task DepartAsync(string nodeId, CancellationToken ct = default);
    Task<IReadOnlyList<(string NodeId, TimeSpan SinceLastSeen)>> DiscoverAsync(string service, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> StatsAsync(string? name = null, CancellationToken ct = default);
}

/// <summary>
/// Owns the single <see cref="ConnectionMultiplexer"/> shared by all Highway
/// traffic on this node, and every wire shape against the 004/004.1 contract.
/// All command names and argument orders live in exactly this class.
///
/// <para><b>Error classification (004.1 contract):</b> server errors starting
/// with <c>ERR HW_</c> are permanent (<see cref="HighwayTransportException"/>);
/// the bare <c>ERR Transaction failed.</c> is a transient watch-conflict abort
/// (<see cref="HighwayTransientException"/>) — safe to retry; anything else is
/// permanent. Transient aborts (and connection interruptions) are retried here
/// with bounded backoff before surfacing.</para>
/// </summary>
internal sealed class HighwayConnection : IHighwayConnection, IAsyncDisposable
{
    /// <summary>Bare transient-abort message emitted by Garnet (004.1 contract).</summary>
    public const string TransientAbortMessage = "ERR Transaction failed.";

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ISubscriber _subscriber;

    private HighwayConnection(ConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _subscriber = redis.GetSubscriber();
    }

    /// <summary>
    /// Connects to the configured server, failing fast with a descriptive
    /// exception — no silent retry loop at startup.
    /// </summary>
    public static async Task<HighwayConnection> ConnectAsync(string configuration, CancellationToken ct = default)
    {
        ConfigurationOptions options;
        try
        {
            options = ConfigurationOptions.Parse(configuration);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"'{configuration}' is not a valid Highway server configuration: {ex.Message}",
                nameof(configuration), ex);
        }

        options.AbortOnConnectFail = true;

        try
        {
            var redis = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
            return new HighwayConnection(redis);
        }
        catch (RedisConnectionException ex)
        {
            throw new HighwayServerUnreachableException(configuration, ex);
        }
    }

    // -------------------------------------------------------------------------
    // RPC commands
    // -------------------------------------------------------------------------

    /// <summary>HW.CALL &lt;service&gt; &lt;requestId&gt; &lt;payload&gt;</summary>
    public Task CallAsync(string service, string requestId, byte[] envelope, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.CALL", service, requestId, envelope).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// HW.DEQUEUE &lt;service&gt; &lt;nodeId&gt; → (requestId, payload), or null when
    /// the queue is empty (the server replies a nil array: RedisResult.IsNull).
    /// </summary>
    public Task<(string RequestId, byte[] Payload)?> DequeueAsync(string service, string nodeId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.DEQUEUE", service, nodeId).ConfigureAwait(false);
            if (result is null || result.IsNull)
                return default((string, byte[])?);

            var arr = (RedisResult[])result!;
            return ((string)arr[0]!, (byte[])arr[1]!);
        }, ct);

    /// <summary>HW.REPLY &lt;requestId&gt; &lt;payload&gt;</summary>
    public Task ReplyAsync(string requestId, byte[] envelope, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.REPLY", requestId, envelope).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>HW.ACK &lt;service&gt; &lt;nodeId&gt; &lt;requestId&gt;</summary>
    public Task AckAsync(string service, string nodeId, string requestId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.ACK", service, nodeId, requestId).ConfigureAwait(false);
            return true;
        }, ct);

    // -------------------------------------------------------------------------
    // Pub/Sub commands
    // -------------------------------------------------------------------------

    /// <summary>HW.PUBLISH &lt;channel&gt; &lt;payload&gt; → group count.</summary>
    public Task<long> PublishCommandAsync(string channel, byte[] envelope, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.PUBLISH", channel, envelope).ConfigureAwait(false);
            return (long)result!;
        }, ct);

    /// <summary>HW.SUBSCRIBE &lt;channel&gt; &lt;group&gt; (idempotent on the server).</summary>
    public Task SubscribeGroupAsync(string channel, string group, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.SUBSCRIBE", channel, group).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// HW.RECEIVE &lt;channel&gt; &lt;group&gt; COUNT n → list of (messageId, payload)
    /// pairs in FIFO order; empty list when nothing is available.
    /// </summary>
    public Task<IReadOnlyList<(long MessageId, byte[] Payload)>> ReceiveAsync(
        string channel, string group, int count, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.RECEIVE", channel, group, "COUNT", count).ConfigureAwait(false);
            if (result is null || result.IsNull)
                return (IReadOnlyList<(long, byte[])>)[];

            var outer = (RedisResult[])result!;
            var entries = new List<(long, byte[])>(outer.Length);
            foreach (var item in outer)
            {
                var pair = (RedisResult[])item!;
                entries.Add((long.Parse((string)pair[0]!), (byte[])pair[1]!));
            }
            return entries;
        }, ct);

    /// <summary>HW.RACK &lt;channel&gt; &lt;group&gt; &lt;messageId&gt;</summary>
    public Task RackAsync(string channel, string group, long messageId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.RACK", channel, group, messageId.ToString()).ConfigureAwait(false);
            return true;
        }, ct);

    // -------------------------------------------------------------------------
    // Reply slots (stock GET/DEL on hw:rep:{requestId}) and doorbells
    // -------------------------------------------------------------------------

    /// <summary>Reads the reply slot; null when absent.</summary>
    public async Task<byte[]?> GetReplySlotAsync(string requestId, CancellationToken ct = default)
    {
        try
        {
            var value = await _db.StringGetAsync($"hw:rep:{requestId}").ConfigureAwait(false);
            return value.HasValue ? (byte[])value! : null;
        }
        catch (RedisException ex)
        {
            throw Classify(ex);
        }
    }

    /// <summary>Deletes the reply slot after retrieval.</summary>
    public async Task DeleteReplySlotAsync(string requestId, CancellationToken ct = default)
    {
        try
        {
            await _db.KeyDeleteAsync($"hw:rep:{requestId}").ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            throw Classify(ex);
        }
    }

    /// <summary>
    /// Subscribes to a doorbell channel. SE.Redis auto-resubscribes after a
    /// reconnect (verified by ClientReconnectTests), so no re-issue logic is needed.
    /// </summary>
    public async Task SubscribeDoorbellAsync(string channel, Action<string> onMessage, CancellationToken ct = default)
    {
        await _subscriber.SubscribeAsync(RedisChannel.Literal(channel), (_, value) =>
        {
            try
            {
                onMessage((string)value!);
            }
            catch
            {
                // A failing handler must never kill the subscription;
                // owners log inside their handlers.
            }
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Registry commands (feature 006)
    // -------------------------------------------------------------------------

    /// <summary>
    /// HW.HEARTBEAT &lt;nodeId&gt; &lt;catalogJson&gt; — the registration form.
    /// Sent once at start and again whenever the server asks for it.
    /// </summary>
    public Task RegisterAsync(string nodeId, byte[] catalogJson, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.HEARTBEAT", nodeId, catalogJson).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// HW.HEARTBEAT &lt;nodeId&gt; — the liveness form. The catalog never crosses
    /// the wire here, so beat cost is independent of catalog size.
    ///
    /// <para><c>+REGISTER</c> is a normal reply, not a failure: the server holds
    /// no record for this node and needs the catalog before it can be discovered
    /// again. It must never be classified as a transport or transient error.</para>
    /// </summary>
    public Task<HeartbeatReply> HeartbeatAsync(string nodeId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.HEARTBEAT", nodeId).ConfigureAwait(false);
            return string.Equals((string?)result, "REGISTER", StringComparison.Ordinal)
                ? HeartbeatReply.ReRegisterRequired
                : HeartbeatReply.Ok;
        }, ct);

    /// <summary>
    /// HW.HEARTBEAT &lt;nodeId&gt; BYE — graceful departure. The node leaves
    /// discovery immediately instead of after the expiry window.
    /// </summary>
    public Task DepartAsync(string nodeId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.HEARTBEAT", nodeId, "BYE").ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// HW.DISCOVER &lt;service&gt; → live nodes hosting the service, with the age
    /// of each one's last beat. Empty when nobody hosts it.
    /// </summary>
    public Task<IReadOnlyList<(string NodeId, TimeSpan SinceLastSeen)>> DiscoverAsync(
        string service, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.DISCOVER", service).ConfigureAwait(false);
            if (result is null || result.IsNull)
                return (IReadOnlyList<(string, TimeSpan)>)[];

            var outer = (RedisResult[])result!;
            var nodes = new List<(string, TimeSpan)>(outer.Length);
            foreach (var item in outer)
            {
                var pair = (RedisResult[])item!;
                var seconds = long.TryParse((string)pair[1]!, out var s) ? s : 0;
                nodes.Add(((string)pair[0]!, TimeSpan.FromSeconds(seconds)));
            }
            return nodes;
        }, ct);

    /// <summary>
    /// HW.STATS [name] → the flat field/value reply as a map. Reply shape is
    /// self-describing so new fields extend the map rather than breaking callers.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> StatsAsync(string? name = null, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = name is null
                ? await _db.ExecuteAsync("HW.STATS").ConfigureAwait(false)
                : await _db.ExecuteAsync("HW.STATS", name).ConfigureAwait(false);

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (result is null || result.IsNull)
                return (IReadOnlyDictionary<string, string>)fields;

            var flat = (RedisResult[])result!;
            for (var i = 0; i + 1 < flat.Length; i += 2)
                fields[(string)flat[i]!] = (string)flat[i + 1]!;

            return fields;
        }, ct);

    // -------------------------------------------------------------------------
    // Send pipeline: bounded transient retry + classification
    // -------------------------------------------------------------------------

    private async Task<T> SendAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (RedisServerException ex)
            {
                if (IsTransient(ex.Message) && attempt < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                throw Classify(ex);
            }
            catch (RedisConnectionException ex)
            {
                // Brief interruption while SE.Redis reconnects — bounded retry.
                if (attempt < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                throw new HighwayTransportException($"Connection to the Highway server failed: {ex.Message}");
            }
        }
    }

    /// <summary>True for the bare watch-conflict abort — the only transient class.</summary>
    public static bool IsTransient(string message)
        => string.Equals(message, TransientAbortMessage, StringComparison.Ordinal);

    /// <summary>Maps a server/connection error to the typed permanent/transient split.</summary>
    public static Exception Classify(RedisException ex)
        => ex is RedisServerException { Message: TransientAbortMessage }
            ? new HighwayTransientException(ex.Message)
            : new HighwayTransportException(ex.Message);

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync().ConfigureAwait(false);
    }
}
