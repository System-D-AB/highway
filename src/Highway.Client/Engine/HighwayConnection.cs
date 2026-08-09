using System.Globalization;

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

    Task<long> PublishCommandAsync(

        string channel, byte[] envelope, DateTimeOffset? deliverAt = null, CancellationToken ct = default);

    Task SubscribeGroupAsync(string channel, string group, CancellationToken ct = default);

    Task<byte[]?> GetReplySlotAsync(string requestId, CancellationToken ct = default);

    Task DeleteReplySlotAsync(string requestId, CancellationToken ct = default);

    Task SubscribeDoorbellAsync(string channel, Action<string> onMessage, CancellationToken ct = default);



    // Queue (feature 014)

    Task QSendAsync(string queue, string messageId, byte[] envelope, DateTimeOffset? deliverAt = null, CancellationToken ct = default);

    Task<(string MessageId, byte[] Payload)?> QClaimAsync(string queue, string nodeId, CancellationToken ct = default);

    Task QAckAsync(string queue, string nodeId, string messageId, CancellationToken ct = default);

    Task SubscribeQueueDoorbellAsync(string queue, Action<string> onMessage, CancellationToken ct = default);



    // Recoverability (feature 015)
    Task<bool> FailAsync(
        string kind, string name, string scope, string id, string exceptionType, byte[] detail,
        CancellationToken ct = default);

    // Deduplication (feature 013)

    Task<IdempotencyClaim> ClaimIdempotencyAsync(string name, string id, TimeSpan window, CancellationToken ct = default);

    Task CompleteIdempotencyAsync(string name, string id, byte[] response, TimeSpan window, CancellationToken ct = default);

    Task ReleaseIdempotencyAsync(string name, string id, CancellationToken ct = default);



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

    public static Task<HighwayConnection> ConnectAsync(string configuration, CancellationToken ct = default)

        => ConnectAsync(configuration, credentials: null, ct);



    /// <summary>

    /// Connects with optional credentials and transport security (feature 012).

    ///

    /// <para><b>Precedence, defined rather than incidental:</b> the connection string is

    /// parsed first, <paramref name="credentials"/> overwrite what it set, and the

    /// caller's <c>ConfigureConnection</c> delegate runs last so it can override anything.

    /// "Which one wins" is the question a developer hits at 2am, so it is answered here

    /// and on each option.</para>

    ///

    /// <para>Every path out of this method redacts the configuration string. It routinely

    /// carries a password now, and an exception message is not a safe place for one.</para>

    /// </summary>

    public static async Task<HighwayConnection> ConnectAsync(

        string configuration, HighwayOptions? credentials, CancellationToken ct = default)

    {

        ConfigurationOptions options;

        try

        {

            options = ConfigurationOptions.Parse(configuration);

        }

        catch (Exception ex)

        {

            throw new ArgumentException(

                $"'{ConnectionStringRedactor.Redact(configuration)}' is not a valid Highway server configuration: {ex.Message}",

                nameof(configuration), ex);

        }



        options.AbortOnConnectFail = true;



        if (credentials is not null)

        {

            if (!string.IsNullOrEmpty(credentials.Username)) options.User = credentials.Username;

            if (!string.IsNullOrEmpty(credentials.Password)) options.Password = credentials.Password;



            if (credentials.Tls is { Enabled: true } tls)

            {

                options.Ssl = true;

                if (!string.IsNullOrEmpty(tls.TargetHost)) options.SslHost = tls.TargetHost;

                if (tls.Protocols is { } protocols) options.SslProtocols = protocols;

            }



            credentials.ConfigureConnection?.Invoke(options);

        }



        try

        {

            var redis = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);

            return new HighwayConnection(redis);

        }

        catch (RedisConnectionException ex) when (IsAuthenticationFailure(ex))

        {

            // Distinguished from an unreachable server because the remedies are opposite:

            // one is "check the network", the other is "check the password". Reporting a

            // wrong password as an unreachable host sends people to the wrong place.

            throw new HighwayAuthenticationException(

                $"The Highway server at '{ConnectionStringRedactor.Redact(configuration)}' rejected the supplied " +

                "credentials. Check the password, and that the server was started with WithPassword.", ex);

        }

        catch (RedisConnectionException ex)

        {

            throw new HighwayServerUnreachableException(ConnectionStringRedactor.Redact(configuration), ex);

        }

    }



    /// <summary>

    /// Whether a connect failure was really an authentication failure. StackExchange.Redis

    /// wraps the server's <c>NOAUTH</c> / <c>WRONGPASS</c> reply in a connection exception,

    /// so the distinction is only visible in the message chain.

    /// </summary>

    private static bool IsAuthenticationFailure(Exception? ex)

    {

        for (var e = ex; e is not null; e = e.InnerException)

        {

            var message = e.Message;

            if (message.Contains("NOAUTH", StringComparison.Ordinal)

                || message.Contains("WRONGPASS", StringComparison.Ordinal)

                || message.Contains("NOPERM", StringComparison.Ordinal))

            {

                return true;

            }

        }

        return false;

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



    /// <summary>

    /// HW.PUBLISH &lt;channel&gt; &lt;payload&gt; [AT &lt;ticks&gt;] → group count.

    ///

    /// <para>The delivery time is sent as an <b>absolute</b> .NET UTC tick count rather

    /// than a relative delay: the server stores what it is told, so a slow round trip

    /// cannot silently extend the delay and AOF replay cannot re-delay from replay time.</para>

    /// </summary>

    public Task<long> PublishCommandAsync(

        string channel, byte[] envelope, DateTimeOffset? deliverAt = null, CancellationToken ct = default)

        => SendAsync(async () =>

        {

            var result = deliverAt is { } at

                ? await _db.ExecuteAsync("HW.PUBLISH", channel, envelope, "AT",

                        at.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false)

                : await _db.ExecuteAsync("HW.PUBLISH", channel, envelope).ConfigureAwait(false);

            return (long)result!;

        }, ct);



    /// <summary>HW.SUBSCRIBE &lt;channel&gt; &lt;group&gt; (idempotent on the server).</summary>

    public Task SubscribeGroupAsync(string channel, string group, CancellationToken ct = default)

        => SendAsync(async () =>

        {

            await _db.ExecuteAsync("HW.SUBSCRIBE", channel, group).ConfigureAwait(false);

            return true;

        }, ct);



    // -------------------------------------------------------------------------

    // Reply slots (stock GET/DEL on hw:rep:{requestId}) and doorbells

    // -------------------------------------------------------------------------



    /// <summary>Reads the reply slot; null when absent.</summary>

    // -------------------------------------------------------------------------

    // Queue (feature 014)

    // -------------------------------------------------------------------------



    /// <summary>HW.QSEND &lt;queue&gt; &lt;messageId&gt; &lt;payload&gt; [AT ticks].</summary>

    public Task QSendAsync(

        string queue, string messageId, byte[] envelope,

        DateTimeOffset? deliverAt = null, CancellationToken ct = default)

        => SendAsync(async () =>

        {

            if (deliverAt is { } at)

            {

                await _db.ExecuteAsync("HW.QSEND", queue, messageId, envelope, "AT",

                    at.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

            }

            else

            {

                await _db.ExecuteAsync("HW.QSEND", queue, messageId, envelope).ConfigureAwait(false);

            }

            return true;

        }, ct);



    /// <summary>HW.QCLAIM &lt;queue&gt; &lt;nodeId&gt; → (messageId, payload), or null when empty.</summary>

    public Task<(string MessageId, byte[] Payload)?> QClaimAsync(

        string queue, string nodeId, CancellationToken ct = default)

        => SendAsync(async () =>

        {

            var result = await _db.ExecuteAsync("HW.QCLAIM", queue, nodeId).ConfigureAwait(false);

            if (result is null || result.IsNull)

                return default((string, byte[])?);



            var arr = (RedisResult[])result!;

            return ((string)arr[0]!, (byte[])arr[1]!);

        }, ct);



    /// <summary>HW.QACK &lt;queue&gt; &lt;nodeId&gt; &lt;messageId&gt;.</summary>

    public Task QAckAsync(string queue, string nodeId, string messageId, CancellationToken ct = default)

        => SendAsync(async () =>

        {

            await _db.ExecuteAsync("HW.QACK", queue, nodeId, messageId).ConfigureAwait(false);

            return true;

        }, ct);



    /// <summary>
    /// HW.FAIL &lt;kind&gt; &lt;name&gt; &lt;scope&gt; &lt;id&gt; &lt;type&gt; &lt;detail&gt; — records why a
    /// handler failed, without acknowledging (feature 015).
    /// </summary>
    /// <returns>True when the message was found and annotated; false when it was already gone.</returns>
    public Task<bool> FailAsync(
        string kind, string name, string scope, string id, string exceptionType, byte[] detail,
        CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.FAIL", kind, name, scope, id, exceptionType, detail)
                .ConfigureAwait(false);
            return (long)result == 1;
        }, ct);

    /// <summary>Subscribes to a queue doorbell — a latency optimisation, never correctness.</summary>

    public async Task SubscribeQueueDoorbellAsync(string queue, Action<string> onMessage, CancellationToken ct = default)

    {

        try

        {

            await _subscriber.SubscribeAsync(

                RedisChannel.Literal($"hw:door:q:{queue}"),

                (_, value) => onMessage(value.ToString() ?? string.Empty)).ConfigureAwait(false);

        }

        catch (RedisException ex)

        {

            throw Classify(ex);

        }

    }



    // -------------------------------------------------------------------------

    // Deduplication (feature 013)

    // -------------------------------------------------------------------------



    /// <summary>Value written while a handler is running, before its response exists.</summary>

    private static readonly byte[] InProgressMarker = " hw:in-progress"u8.ToArray();



    private static string IdempotencyKey(string name, string id) => $"hw:idem:{name}:{id}";



    /// <summary>

    /// Attempts to claim the right to run a handler for one delivery.

    ///

    /// <para><c>SET NX EX</c> is atomic, so two concurrent redeliveries cannot both claim.

    /// The loser reads what the winner left: a response if the handler finished, or the

    /// in-progress marker if it is still running — or crashed.</para>

    /// </summary>

    public async Task<IdempotencyClaim> ClaimIdempotencyAsync(

        string name, string id, TimeSpan window, CancellationToken ct = default)

    {

        try

        {

            var key = IdempotencyKey(name, id);



            if (await _db.StringSetAsync(key, InProgressMarker, window, When.NotExists).ConfigureAwait(false))

                return IdempotencyClaim.Claimed();



            var prior = await _db.StringGetAsync(key).ConfigureAwait(false);

            if (!prior.HasValue)

            {

                // Expired between the SET NX and the GET. Racing an expiry is not worth a

                // second round trip: treat it as ours and run.

                return IdempotencyClaim.Claimed();

            }



            var bytes = (byte[])prior!;

            return bytes.AsSpan().SequenceEqual(InProgressMarker)

                ? IdempotencyClaim.InProgress()

                : IdempotencyClaim.Duplicate(bytes);

        }

        catch (RedisException ex)

        {

            throw Classify(ex);

        }

    }



    /// <summary>Replaces the in-progress marker with the handler's response.</summary>

    public async Task CompleteIdempotencyAsync(

        string name, string id, byte[] response, TimeSpan window, CancellationToken ct = default)

    {

        try

        {

            await _db.StringSetAsync(IdempotencyKey(name, id), response, window).ConfigureAwait(false);

        }

        catch (RedisException ex)

        {

            throw Classify(ex);

        }

    }



    /// <summary>

    /// Drops the marker so the delivery can be retried immediately.

    ///

    /// <para>Used when the handler could not be run at all — a transport failure before

    /// any work happened. Leaving the marker would block the redelivery for the whole

    /// window over something that never ran.</para>

    /// </summary>

    public async Task ReleaseIdempotencyAsync(string name, string id, CancellationToken ct = default)

    {

        try

        {

            await _db.KeyDeleteAsync(IdempotencyKey(name, id)).ConfigureAwait(false);

        }

        catch (RedisException)

        {

            // Best effort. The marker expires on its own.

        }

    }



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



    /// <summary>

    /// Maps a server/connection error to the typed permanent/transient split.

    ///

    /// <para>Authentication and authorization failures are <b>permanent</b>: retrying a

    /// wrong password wastes the backoff budget and trips attempt counters on systems that

    /// keep them. They are given their own types because the remedy differs from every

    /// other permanent failure — one is a configuration problem, not a code or network

    /// problem.</para>

    /// </summary>

    public static Exception Classify(RedisException ex)

    {

        if (ex is RedisServerException { Message: TransientAbortMessage })

            return new HighwayTransientException(ex.Message);



        var message = ex.Message;



        if (message.StartsWith("NOAUTH", StringComparison.Ordinal))

        {

            return new HighwayAuthenticationException(

                "The Highway server requires authentication and none was supplied. " +

                "Set HighwayOptions.Password.", ex);

        }



        if (message.StartsWith("WRONGPASS", StringComparison.Ordinal))

        {

            return new HighwayAuthenticationException(

                "The Highway server rejected the supplied credentials.", ex);

        }



        if (message.StartsWith("NOPERM", StringComparison.Ordinal))

        {

            // Garnet's reply does not name the command — it is literally

            // "NOPERM this user has no permissions to run the command" — so the caller

            // attaches it. Parsing it out would produce null every time.

            return new HighwayAuthorizationException(command: null, ex);

        }



        return new HighwayTransportException(message);

    }



    public async ValueTask DisposeAsync()

    {

        await _redis.DisposeAsync().ConfigureAwait(false);

    }

}

