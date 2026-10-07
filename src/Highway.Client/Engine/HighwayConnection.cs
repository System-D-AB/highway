using System.Globalization;
using Highway.Abstractions;
using Highway.Client.Wire;
using StackExchange.Redis;

namespace Highway.Client.Engine;

/// <summary>Outcome of the <c>HW.HEARTBEAT</c> liveness form.</summary>
internal enum HeartbeatReply
{
    /// <summary>Liveness refreshed.</summary>
    Ok,

    /// <summary>
    /// <c>+SESSION</c>: the broker holds no session for this node — it was never established, was
    /// pruned, or the broker lost it (a wipe, a fresh node, a re-bootstrap). The node must send
    /// <c>HW.SESSION</c> before it is discoverable, subscribed, or scheduling jobs again.
    /// </summary>
    SessionRequired,
}

/// <summary>Which kind of work box a claim reads (protocol 5.0 target grammar: <c>SVC</c> / <c>Q</c>).</summary>
internal enum WorkKind { Service, Queue }

/// <summary>
/// One claimed message: the opaque <see cref="Token"/> the broker issued (echoed verbatim to settle it),
/// the message's id, and its payload.
/// </summary>
internal readonly record struct ClaimedWork(string Token, string Id, byte[] Payload);
internal readonly record struct DeadLetterResolutionResult(bool Held, string Status, string Detail);

/// <summary>Raw representation of an entry returned by HW.DLQ PEEK.</summary>
public sealed record RawDeadLetterPeekEntry(
    string DeadLetterId,
    DateTimeOffset DeadLetteredAt,
    int Attempts,
    int Requeues,
    string Reason,
    string Id,
    byte[] Payload,
    FailureInfo? LastFailure,
    FailureInfo? FirstFailure);

/// <summary>
/// The wire surface of <see cref="HighwayConnection"/> — every HW.* command shape and doorbell
/// subscription (protocol 5.0). Loops, the registry, and the client depend on this interface (mockable
/// in unit tests); the command names and argument orders live in exactly one place:
/// <see cref="HighwayConnection"/>.
/// </summary>
internal interface IHighwayConnection
{
    // Work: enqueue
    Task CallAsync(string service, string requestId, byte[] envelope, CancellationToken ct = default);
    Task QSendAsync(string queue, string messageId, byte[] envelope, DateTimeOffset? deliverAt = null, CancellationToken ct = default);
    Task<long> PublishCommandAsync(string channel, byte[] envelope, DateTimeOffset? deliverAt = null, CancellationToken ct = default);

    // Work: claim and settle (one path for services, queues and group queues)
    Task<ClaimedWork?> ClaimAsync(WorkKind kind, string name, string nodeId, CancellationToken ct = default);
    Task<bool> AckAsync(string token, CancellationToken ct = default);

    /// <summary>HW.TOUCH — renew a claimed message's lease (019). True when it was renewed.</summary>
    Task<bool> TouchAsync(string token, CancellationToken ct = default);

    /// <summary>HW.FAIL — record why a handler failed, without acknowledging (015). True when recorded.</summary>
    Task<bool> FailAsync(string token, string exceptionType, byte[] detail, CancellationToken ct = default);

    // RPC reply
    Task ReplyAsync(string requestId, byte[] envelope, CancellationToken ct = default);

    /// <summary>HW.RESULT — takes (reads and removes) a waiting reply, or null when none is waiting.</summary>
    Task<byte[]?> TakeReplyAsync(string requestId, CancellationToken ct = default);

    // Doorbells (latency only, never correctness)
    Task SubscribeDoorbellAsync(string channel, Action<string> onMessage, CancellationToken ct = default);
    Task SubscribeQueueDoorbellAsync(string queue, Action<string> onMessage, CancellationToken ct = default);
    Task SubscribeDeadLetterDoorbellAsync(string target, Action<string> onNotification, CancellationToken ct = default);

    // Dead letters (feature 073)
    Task<ClaimedWork?> ClaimDeadAsync(WorkKind kind, string name, string nodeId, CancellationToken ct = default);
    Task<DeadLetterResolutionResult> ResolveDeadLetterAsync(string token, DeadLetterAction action, CancellationToken ct = default);
    Task<IReadOnlyList<RawDeadLetterPeekEntry>> DlqPeekAsync(string targetKind, string targetName, int count, CancellationToken ct = default);
    Task<long> DlqCountAsync(string targetKind, string targetName, CancellationToken ct = default);
    Task<bool> DlqRequeueAsync(string targetKind, string targetName, string deadLetterId, TimeSpan? after = null, CancellationToken ct = default);
    Task<int> DlqRequeueAllAsync(string targetKind, string targetName, int max, TimeSpan? after = null, CancellationToken ct = default);
    Task<bool> DlqPurgeAsync(string targetKind, string targetName, string deadLetterId, CancellationToken ct = default);
    Task<int> DlqPurgeAllAsync(string targetKind, string targetName, int max, CancellationToken ct = default);

    // Deduplication (feature 013)
    Task<IdempotencyClaim> ClaimIdempotencyAsync(string name, string id, TimeSpan window, CancellationToken ct = default);
    Task CompleteIdempotencyAsync(string name, string id, byte[] response, TimeSpan window, CancellationToken ct = default);
    Task ReleaseIdempotencyAsync(string name, string id, CancellationToken ct = default);

    // Session and registry
    /// <summary>HW.SESSION — declares everything the broker holds for this node: catalog, subscriptions, jobs.</summary>
    Task EstablishSessionAsync(string nodeId, byte[] sessionJson, CancellationToken ct = default);
    Task<HeartbeatReply> HeartbeatAsync(string nodeId, CancellationToken ct = default);
    Task DepartAsync(string nodeId, CancellationToken ct = default);

    /// <summary>HW.HEARTBEAT &lt;node&gt; BYE PURGE — retire forever (017). Returns what it destroyed.</summary>
    Task<(int Groups, long Messages, long Bytes)> PurgeAsync(string nodeId, CancellationToken ct = default);
    Task<IReadOnlyList<(string NodeId, TimeSpan SinceLastSeen)>> DiscoverAsync(string service, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> StatsAsync(string? name = null, CancellationToken ct = default);
}

/// <summary>
/// Owns the single <see cref="ConnectionMultiplexer"/> shared by all Highway traffic on this node, and
/// every wire shape of protocol 5.0. All command names and argument orders live in exactly this class.
///
/// <para><b>Error classification:</b> server errors starting with <c>ERR HW_</c> are permanent
/// (<see cref="HighwayTransportException"/>); <c>NOTPRIMARY</c> is failover (follow it and retry); a lost
/// or disposed connection is retried with bounded backoff, failing over first when other endpoints are
/// configured. Anything else is permanent.</para>
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

    private ConnectionMultiplexer _redis;
    private IDatabase _db;
    private ISubscriber _subscriber;
    private readonly HighwayConnectionSource? _source;
    private readonly List<(string Channel, Action<string> OnMessage)> _doorbells = [];

    /// <summary>
    /// Exposes the underlying multiplexer so that <c>HighwayCache</c> can share
    /// the same connection without creating a second one (feature 026, D1).
    /// </summary>
    internal IConnectionMultiplexer Multiplexer => _redis;

    internal static HighwayConnection FromMultiplexer(IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        return new HighwayConnection((ConnectionMultiplexer)redis, source: null);
    }

    internal static HighwayConnection FromSource(HighwayConnectionSource source, IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(redis);
        return new HighwayConnection((ConnectionMultiplexer)redis, source);
    }

    // ---- the herd triggers (042-1b B-T3) -------------------------------------

    private readonly TimeSpan _masterHealthTimeout;
    private readonly Timer? _healthTicker;
    private long _lastSuccessTicks;
    private int _walking;

    /// <summary>
    /// Raised after this connection has converged on a (possibly new) master — the
    /// engine re-drives pending RPCs on it (042-1b B-T5 / parent R4).
    /// </summary>
    internal event Action? Converged;

    private void NoteSuccess() => Volatile.Write(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);

    private void SubscribeTopology(ISubscriber subscriber)
        => subscriber.Subscribe(RedisChannel.Literal("hw:door:topology"), (channel, message) =>
        {
            var text = (string?)message;
            if (text is null) return;

            if (text.StartsWith("GOODBYE", StringComparison.Ordinal))
            {
                // The incumbent said go: walk NOW, excluding it — R12's fast path. The
                // successor may need a moment to turn willing (its own GOODBYE arrives
                // on a different connection), so this walk retries across the drain.
                _ = WalkAsync(excludeCurrent: true, attempts: 40, delayMs: 250);
            }
            else
            {
                // TOPOLOGY / ROSTER-UPDATE are advisory: refresh the roster; stay put.
                _ = _source!.RefreshRosterAsync();
            }
        });

    private int _healthProbing;

    private void OnHealthTick()
    {
        var silence = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _lastSuccessTicks));
        if (silence < _masterHealthTimeout)
            return;
        if (Interlocked.CompareExchange(ref _healthProbing, 1, 0) != 0)
            return;
        _ = ProbeHealthAsync();
    }

    /// <summary>
    /// Idle is not frozen: a quiet client PINGs first, and only a failed/late probe
    /// walks. A healthy-but-unused master keeps its herd.
    /// </summary>
    private async Task ProbeHealthAsync()
    {
        try
        {
            var ping = _db.PingAsync();
            var winner = await Task.WhenAny(ping, Task.Delay(1_000)).ConfigureAwait(false);
            if (winner == ping && ping.IsCompletedSuccessfully)
            {
                NoteSuccess();
                return;
            }
            await WalkAsync(excludeCurrent: true).ConfigureAwait(false);
        }
        catch
        {
            await WalkAsync(excludeCurrent: true).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _healthProbing, 0);
        }
    }

    /// <summary>
    /// One serialized walk (the source's gate serializes across operations; this flag
    /// stops the ticker stacking walks). On success: adopt, resubscribe, raise
    /// <see cref="Converged"/> (via <see cref="AdoptCurrentMultiplexer"/>).
    /// <paramref name="attempts"/> &gt; 1 keeps trying across a convergence window
    /// (the GOODBYE shape, where the successor turns willing moments later).
    /// </summary>
    private async Task WalkAsync(bool excludeCurrent, int attempts = 1, int delayMs = 0)
    {
        if (_source is null || Interlocked.CompareExchange(ref _walking, 1, 0) != 0)
            return;
        try
        {
            for (var i = 0; i < attempts; i++)
            {
                if (await _source.TryFailoverAsync(default, excludeCurrent).ConfigureAwait(false))
                {
                    AdoptCurrentMultiplexer();
                    NoteSuccess();
                    return;
                }
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort from these triggers; the per-operation bounded retry remains
            // the guarantee.
        }
        finally
        {
            Interlocked.Exchange(ref _walking, 0);
        }
    }

    /// <summary>
    /// TEST SEAM ONLY (074): a connection over a bare <see cref="IDatabase"/>, so a unit test can observe the
    /// exact arguments and retries of a command. No multiplexer, no subscriber, no source — only the
    /// request/response commands work through it.
    /// </summary>
    internal HighwayConnection(IDatabase db)
    {
        _redis = null!;
        _db = db;
        _subscriber = null!;
        _lastSuccessTicks = DateTime.UtcNow.Ticks;
    }

    private HighwayConnection(ConnectionMultiplexer redis, HighwayConnectionSource? source = null)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _subscriber = redis.GetSubscriber();
        _source = source;
        _lastSuccessTicks = DateTime.UtcNow.Ticks;

        if (source is not null)
        {
            _masterHealthTimeout = source.Settings is HighwayOptions ho && ho.MasterHealthTimeout > TimeSpan.Zero
                ? ho.MasterHealthTimeout
                : TimeSpan.FromSeconds(3);

            SubscribeTopology(_subscriber);

            // The health ticker (042-1b B-T3c / parent R3.3): a frozen, TCP-alive master
            // produces neither a drop nor a narration — this covers the ceiling `x`.
            _healthTicker = new Timer(_ => OnHealthTick(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
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
        var options = HighwayConnectionConfiguration.Build(configuration, credentials);
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

    internal static ConfigurationOptions BuildConfigurationOptions(string configuration, IHighwayConnectionSettings? credentials)
        => HighwayConnectionConfiguration.Build(configuration, credentials);

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
    /// HW.CLAIM SVC|Q &lt;name&gt; &lt;nodeId&gt; → one claimed message, or null when nothing is due (an empty
    /// array). The token is the claim's handle for ack / touch / fail.
    /// </summary>
    public Task<ClaimedWork?> ClaimAsync(WorkKind kind, string name, string nodeId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.CLAIM", kind == WorkKind.Service ? "SVC" : "Q", name, nodeId).ConfigureAwait(false);
            if (result is null || result.IsNull) return default(ClaimedWork?);
            var rows = (RedisResult[])result!;
            if (rows.Length == 0) return default(ClaimedWork?);
            var row = (RedisResult[])rows[0]!;
            return new ClaimedWork((string)row[0]!, (string)row[1]!, (byte[])row[2]!);
        }, ct);

    /// <summary>HW.ACK &lt;token&gt; → true when removed; false when it was no longer held (a late ack is not an error).</summary>
    public Task<bool> AckAsync(string token, CancellationToken ct = default)
        => SendAsync(async () => (long)(await _db.ExecuteAsync("HW.ACK", token).ConfigureAwait(false)) == 1, ct);

    /// <summary>HW.TOUCH &lt;token&gt; — "still working", so a slow handler is not duplicated underneath itself (019).</summary>
    public Task<bool> TouchAsync(string token, CancellationToken ct = default)
        => SendAsync(async () => (long)(await _db.ExecuteAsync("HW.TOUCH", token).ConfigureAwait(false)) == 1, ct);

    /// <summary>HW.FAIL &lt;token&gt; &lt;type&gt; &lt;detail&gt; — records why a handler failed, without acknowledging (015).</summary>
    public Task<bool> FailAsync(string token, string exceptionType, byte[] detail, CancellationToken ct = default)
        => SendAsync(async () =>
            (long)(await _db.ExecuteAsync("HW.FAIL", token, exceptionType, detail).ConfigureAwait(false)) == 1, ct);

    /// <summary>
    /// HW.CLAIM DEAD SVC|Q &lt;name&gt; &lt;nodeId&gt; → one claimed dead-letter notification, or null when nothing is due.
    /// </summary>
    public Task<ClaimedWork?> ClaimDeadAsync(WorkKind kind, string name, string nodeId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.CLAIM", "DEAD", kind == WorkKind.Service ? "SVC" : "Q", name, nodeId).ConfigureAwait(false);
            if (result is null || result.IsNull) return default(ClaimedWork?);
            var rows = (RedisResult[])result!;
            if (rows.Length == 0) return default(ClaimedWork?);
            var row = (RedisResult[])rows[0]!;
            return new ClaimedWork((string)row[0]!, (string)row[1]!, (byte[])row[2]!);
        }, ct);

    /// <summary>
    /// HW.DLQ RESOLVE &lt;token&gt; KEEP|DISCARD|REQUEUE [AFTER &lt;ms&gt;]
    /// </summary>
    public Task<DeadLetterResolutionResult> ResolveDeadLetterAsync(string token, DeadLetterAction action, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            RedisResult result;
            if (action.Type == DeadLetterAction.ActionType.Keep)
            {
                result = await _db.ExecuteAsync("HW.DLQ", "RESOLVE", token, "KEEP").ConfigureAwait(false);
            }
            else if (action.Type == DeadLetterAction.ActionType.Discard)
            {
                result = await _db.ExecuteAsync("HW.DLQ", "RESOLVE", token, "DISCARD").ConfigureAwait(false);
            }
            else // Requeue
            {
                if (action.After is { } after && after > TimeSpan.Zero)
                {
                    var ms = (long)after.TotalMilliseconds;
                    if (ms < 1) ms = 1;
                    result = await _db.ExecuteAsync("HW.DLQ", "RESOLVE", token, "REQUEUE", "AFTER", ms.ToString()).ConfigureAwait(false);
                }
                else
                {
                    result = await _db.ExecuteAsync("HW.DLQ", "RESOLVE", token, "REQUEUE").ConfigureAwait(false);
                }
            }

            if (result is null || result.IsNull) return new DeadLetterResolutionResult(false, "", "");
            if (result.Resp2Type == ResultType.Integer)
            {
                var val = (long)result;
                return new DeadLetterResolutionResult(val != 0, val != 0 ? "done" : "notheld", "");
            }
            if (result.Resp2Type == ResultType.Array)
            {
                var rows = (RedisResult[])result!;
                var status = rows.Length > 0 ? (string)rows[0]! : "";
                var detail = rows.Length > 1 ? (string)rows[1]! : "";
                return new DeadLetterResolutionResult(true, status, detail);
            }
            return new DeadLetterResolutionResult(true, result.ToString() ?? "", "");
        }, ct);

    /// <summary>HW.DLQ PEEK &lt;targetKind&gt; &lt;targetName&gt; COUNT &lt;count&gt;</summary>
    public Task<IReadOnlyList<RawDeadLetterPeekEntry>> DlqPeekAsync(string targetKind, string targetName, int count, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.DLQ", "PEEK", targetKind, targetName, "COUNT", count.ToString()).ConfigureAwait(false);
            if (result is null || result.IsNull || result.Resp2Type != ResultType.Array)
                return (IReadOnlyList<RawDeadLetterPeekEntry>)Array.Empty<RawDeadLetterPeekEntry>();

            var rows = (RedisResult[])result!;
            var list = new List<RawDeadLetterPeekEntry>(rows.Length);
            foreach (var rowObj in rows)
            {
                if (rowObj.Resp2Type != ResultType.Array) continue;
                var row = (RedisResult[])rowObj!;

                string deadLetterId = "";
                DateTimeOffset deadLetteredAt = default;
                int attempts = 0;
                int requeues = 0;
                string reason = "";
                string id = "";
                byte[] payload = [];
                string? failureType = null;
                string? failureFirstType = null;
                string? failureDetail = null;

                for (var i = 0; i + 1 < row.Length; i += 2)
                {
                    var key = (string)row[i]!;
                    var val = row[i + 1];
                    switch (key)
                    {
                        case "deadId":
                            deadLetterId = (string)val!;
                            break;
                        case "deadLetteredAt":
                            if (DateTimeOffset.TryParse((string)val!, out var dto))
                                deadLetteredAt = dto;
                            break;
                        case "attempts":
                            int.TryParse((string)val!, out attempts);
                            break;
                        case "requeues":
                            int.TryParse((string)val!, out requeues);
                            break;
                        case "reason":
                            reason = (string)val!;
                            break;
                        case "messageId":
                        case "requestId":
                            id = (string)val!;
                            break;
                        case "payload":
                            payload = (byte[])val!;
                            break;
                        case "failureType":
                            failureType = (string)val!;
                            break;
                        case "failureFirstType":
                            failureFirstType = (string)val!;
                            break;
                        case "failureDetail":
                            failureDetail = System.Text.Encoding.UTF8.GetString((byte[])val!);
                            break;
                    }
                }

                FailureInfo? lastFailure = failureType is not null ? new FailureInfo(failureType, failureDetail ?? "") : null;
                FailureInfo? firstFailure = failureFirstType is not null ? new FailureInfo(failureFirstType, failureDetail ?? "") : null;

                list.Add(new RawDeadLetterPeekEntry(
                    deadLetterId,
                    deadLetteredAt,
                    attempts,
                    requeues,
                    reason,
                    id,
                    payload,
                    lastFailure,
                    firstFailure));
            }

            return (IReadOnlyList<RawDeadLetterPeekEntry>)list;
        }, ct);

    /// <summary>HW.DLQ COUNT &lt;targetKind&gt; &lt;targetName&gt;</summary>
    public Task<long> DlqCountAsync(string targetKind, string targetName, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.DLQ", "COUNT", targetKind, targetName).ConfigureAwait(false);
            if (result is null || result.IsNull) return 0L;
            return (long)result;
        }, ct);

    /// <summary>HW.DLQ REQUEUE &lt;targetKind&gt; &lt;targetName&gt; ID &lt;deadLetterId&gt; [AFTER &lt;ms&gt;]</summary>
    public Task<bool> DlqRequeueAsync(string targetKind, string targetName, string deadLetterId, TimeSpan? after = null, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            RedisResult result;
            if (after is { } delay && delay > TimeSpan.Zero)
            {
                var ms = (long)delay.TotalMilliseconds;
                if (ms < 1) ms = 1;
                result = await _db.ExecuteAsync("HW.DLQ", "REQUEUE", targetKind, targetName, "ID", deadLetterId, "AFTER", ms.ToString()).ConfigureAwait(false);
            }
            else
            {
                result = await _db.ExecuteAsync("HW.DLQ", "REQUEUE", targetKind, targetName, "ID", deadLetterId).ConfigureAwait(false);
            }

            if (result is null || result.IsNull) return false;
            return (long)result > 0;
        }, ct);

    /// <summary>HW.DLQ REQUEUE &lt;targetKind&gt; &lt;targetName&gt; COUNT &lt;max&gt; [AFTER &lt;ms&gt;]</summary>
    public Task<int> DlqRequeueAllAsync(string targetKind, string targetName, int max, TimeSpan? after = null, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            RedisResult result;
            if (after is { } delay && delay > TimeSpan.Zero)
            {
                var ms = (long)delay.TotalMilliseconds;
                if (ms < 1) ms = 1;
                result = await _db.ExecuteAsync("HW.DLQ", "REQUEUE", targetKind, targetName, "COUNT", max.ToString(), "AFTER", ms.ToString()).ConfigureAwait(false);
            }
            else
            {
                result = await _db.ExecuteAsync("HW.DLQ", "REQUEUE", targetKind, targetName, "COUNT", max.ToString()).ConfigureAwait(false);
            }

            if (result is null || result.IsNull) return 0;
            return (int)(long)result;
        }, ct);

    /// <summary>HW.DLQ PURGE &lt;targetKind&gt; &lt;targetName&gt; ID &lt;deadLetterId&gt;</summary>
    public Task<bool> DlqPurgeAsync(string targetKind, string targetName, string deadLetterId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.DLQ", "PURGE", targetKind, targetName, "ID", deadLetterId).ConfigureAwait(false);
            if (result is null || result.IsNull) return false;
            return (long)result > 0;
        }, ct);

    /// <summary>HW.DLQ PURGE &lt;targetKind&gt; &lt;targetName&gt; COUNT &lt;max&gt;</summary>
    public Task<int> DlqPurgeAllAsync(string targetKind, string targetName, int max, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.DLQ", "PURGE", targetKind, targetName, "COUNT", max.ToString()).ConfigureAwait(false);
            if (result is null || result.IsNull) return 0;
            return (int)(long)result;
        }, ct);

    /// <summary>HW.REPLY &lt;requestId&gt; &lt;payload&gt;</summary>
    public Task ReplyAsync(string requestId, byte[] envelope, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.REPLY", requestId, envelope).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// HW.RESULT &lt;requestId&gt; → the reply, removed in the same command, or null when none is waiting.
    /// Routed through the send pipeline, so it retries and follows <c>NOTPRIMARY</c> like every command
    /// (the stock GET/DEL it replaces did neither).
    /// </summary>
    public Task<byte[]?> TakeReplyAsync(string requestId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.RESULT", requestId).ConfigureAwait(false);
            return result is null || result.IsNull ? null : (byte[]?)result;
        }, ct);

    // -------------------------------------------------------------------------
    // Pub/Sub commands
    // -------------------------------------------------------------------------

    /// <summary>
    /// HW.PUBLISH &lt;channel&gt; &lt;payload&gt; ID &lt;publishId&gt; [AT &lt;ticks&gt;] → group count.
    ///
    /// <para>The delivery time is sent as an <b>absolute</b> .NET UTC tick count rather
    /// than a relative delay: the server stores what it is told, so a slow round trip
    /// cannot silently extend the delay and AOF replay cannot re-delay from replay time.</para>
    ///
    /// <para><b>One publish id per call</b> (074 Part B): created here, outside the retry loop, so every
    /// retry of this call carries the same id and the broker delivers a publish whose reply was lost
    /// only once.</para>
    /// </summary>
    public Task<long> PublishCommandAsync(
        string channel, byte[] envelope, DateTimeOffset? deliverAt = null, CancellationToken ct = default)
    {
        var publishId = Guid.NewGuid().ToString("N");
        return SendAsync(async () =>
        {
            var result = deliverAt is { } at
                ? await _db.ExecuteAsync("HW.PUBLISH", channel, envelope, "ID", publishId, "AT",
                        at.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false)
                : await _db.ExecuteAsync("HW.PUBLISH", channel, envelope, "ID", publishId).ConfigureAwait(false);
            return (long)result!;
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Queue (feature 014)
    // -------------------------------------------------------------------------

    /// <summary>HW.SEND &lt;queue&gt; &lt;messageId&gt; &lt;payload&gt; [AT ticks].</summary>
    public Task QSendAsync(
        string queue, string messageId, byte[] envelope,
        DateTimeOffset? deliverAt = null, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            if (deliverAt is { } at)
                await _db.ExecuteAsync("HW.SEND", queue, messageId, envelope, "AT",
                    at.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            else
                await _db.ExecuteAsync("HW.SEND", queue, messageId, envelope).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// Subscribes to a queue doorbell — a latency optimisation, never correctness. Tracked like every
    /// other doorbell, so a failover re-subscribes it (before 3.0 it was lost, and queue workers fell back
    /// to backstop polling after the first failover).
    /// </summary>
    public Task SubscribeQueueDoorbellAsync(string queue, Action<string> onMessage, CancellationToken ct = default)
        => SubscribeDoorbellAsync($"hw:door:q:{queue}", onMessage, ct);

    /// <summary>
    /// Subscribes to a dead-letter doorbell — a latency optimisation, never correctness (073).
    /// </summary>
    public Task SubscribeDeadLetterDoorbellAsync(string target, Action<string> onNotification, CancellationToken ct = default)
        => SubscribeDoorbellAsync($"hw:door:dead:{target}", onNotification, ct);

    // -------------------------------------------------------------------------
    // Deduplication (feature 013)
    // -------------------------------------------------------------------------

    private static long WindowSeconds(TimeSpan window) => Math.Max(1, (long)Math.Ceiling(window.TotalSeconds));

    /// <summary>
    /// HW.IDEM BEGIN — claims the right to run a handler for one delivery. Atomic on the broker, so two
    /// concurrent redeliveries cannot both claim: the loser learns whether the winner finished (its cached
    /// result) or is still running — or crashed, until the window lapses.
    /// </summary>
    public Task<IdempotencyClaim> ClaimIdempotencyAsync(
        string name, string id, TimeSpan window, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.IDEM", "BEGIN", name, id,
                WindowSeconds(window).ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            if (result.Resp2Type == ResultType.Array)
                return IdempotencyClaim.Duplicate((byte[])((RedisResult[])result!)[1]!);
            return string.Equals((string?)result, "BUSY", StringComparison.Ordinal)
                ? IdempotencyClaim.InProgress()
                : IdempotencyClaim.Claimed();
        }, ct);

    /// <summary>HW.IDEM END — replaces the in-progress claim with the handler's response for the window.</summary>
    public Task CompleteIdempotencyAsync(
        string name, string id, byte[] response, TimeSpan window, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.IDEM", "END", name, id,
                WindowSeconds(window).ToString(CultureInfo.InvariantCulture), response).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// HW.IDEM ABORT — drops the claim so the delivery can be retried at once. Used when the handler could
    /// not run at all; leaving the claim would block the redelivery for the whole window over something that
    /// never ran. Best effort: the claim expires on its own.
    /// </summary>
    public async Task ReleaseIdempotencyAsync(string name, string id, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(async () =>
            {
                await _db.ExecuteAsync("HW.IDEM", "ABORT", name, id).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HighwayTransportException or HighwayTransientException)
        {
            // Best effort. The claim expires on its own.
        }
    }

    /// <summary>
    /// Subscribes to a doorbell channel. SE.Redis auto-resubscribes after a
    /// reconnect (verified by ClientReconnectTests), so no re-issue logic is needed.
    /// </summary>
    public async Task SubscribeDoorbellAsync(string channel, Action<string> onMessage, CancellationToken ct = default)
    {
        _doorbells.Add((channel, onMessage));
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
    /// HW.SESSION &lt;nodeId&gt; &lt;sessionJson&gt; — declares the catalog, group subscriptions and job schedules
    /// the broker holds for this node, in one idempotent command. Sent at start, after a failover adoption,
    /// and whenever a heartbeat answers <c>+SESSION</c>.
    /// </summary>
    public Task EstablishSessionAsync(string nodeId, byte[] sessionJson, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            await _db.ExecuteAsync("HW.SESSION", nodeId, sessionJson).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// HW.HEARTBEAT &lt;nodeId&gt; — the liveness form: 8 bytes on the broker, independent of catalog size.
    ///
    /// <para><c>+SESSION</c> is a normal reply, not a failure: the broker holds no session for this node and
    /// needs it declared again. It must never be classified as a transport or transient error.</para>
    /// </summary>
    public Task<HeartbeatReply> HeartbeatAsync(string nodeId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = await _db.ExecuteAsync("HW.HEARTBEAT", nodeId).ConfigureAwait(false);
            return string.Equals((string?)result, "SESSION", StringComparison.Ordinal)
                ? HeartbeatReply.SessionRequired
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
    /// HW.HEARTBEAT &lt;node&gt; BYE PURGE — the node is never coming back (017).
    ///
    /// <para>Unlike <see cref="DepartAsync"/>, this destroys the node's subscriber queues.
    /// It returns what it destroyed so an irreversible act leaves a record.</para>
    /// </summary>

    public Task<(int Groups, long Messages, long Bytes)> PurgeAsync(string nodeId, CancellationToken ct = default)
        => SendAsync(async () =>
        {
            var result = (RedisResult[])(await _db
                .ExecuteAsync("HW.HEARTBEAT", nodeId, "BYE", "PURGE").ConfigureAwait(false))!;
            return ((int)(long)result[0], (long)result[1], (long)result[2]);
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
                var result = await operation().ConfigureAwait(false);
                NoteSuccess();   // the herd health clock (042-1b): a completed exchange
                return result;
            }
            catch (RedisServerException ex)
            {
                if (TrySwitchOnNotPrimary(ex.Message))
                {
                    if (attempt < RetryDelays.Length)
                    {
                        await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                        attempt++;
                        continue;
                    }
                }

                if (IsTransient(ex.Message) && attempt < RetryDelays.Length)
                {
                    await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                throw Classify(ex);
            }
            catch (Exception ex) when (ex is RedisConnectionException or ObjectDisposedException)
            {
                // The active host is gone — or its multiplexer was disposed under an operation that
                // outlived a failover (069 L4: an ObjectDisposedException used to escape the retry). With a multi-endpoint connection string, probe
                // the others for the writable node before burning a plain retry (042 R6.1)
                // — the path a killed primary takes, where no -NOTPRIMARY can be spoken.
                // A single-endpoint client falls through to the bounded retry.
                if (attempt < RetryDelays.Length
                    && await TryFailoverOnConnectionLossAsync(ct).ConfigureAwait(false))
                {
                    attempt++;
                    continue;
                }

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

    /// <summary>042 T6: <c>NOTPRIMARY &lt;endpoint&gt; &lt;epoch&gt;</c> — not an ERR HW_ prefix.</summary>
    public static bool TryParseNotPrimary(string message, out string endpoint, out ulong epoch)
    {
        endpoint = "";
        epoch = 0;
        var text = message.StartsWith("ERR ", StringComparison.Ordinal) ? message[4..] : message;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("NOTPRIMARY", StringComparison.Ordinal))
            return false;
        endpoint = parts[1];
        return ulong.TryParse(parts[2], out epoch);
    }

    private bool TrySwitchOnNotPrimary(string message)
    {
        if (_source is null || !TryParseNotPrimary(message, out var endpoint, out var epoch))
            return false;
        _source.NoteObservedEpoch(epoch);

        // A stood-down node that never learned its successor redirects to ITSELF —
        // following that is a loop. Walk the roster instead (excluding it).
        if (EndpointIdentity.Same(HighwayConnectionSource.HostOf(_source.ActiveServer), endpoint))
        {
            _ = WalkAsync(excludeCurrent: true, attempts: 8, delayMs: 250);
            return true;   // the retry loop backs off while the walk lands
        }

        _source.SwitchTo(endpoint);
        AdoptCurrentMultiplexer();
        return true;
    }

    /// <summary>
    /// 042 R6.1, the dead-primary half: the active host stopped answering entirely (no
    /// <c>-NOTPRIMARY</c> to follow), so ask the source to probe the other configured
    /// endpoints for the writable node. Only meaningful with a multi-endpoint connection
    /// string; a single-endpoint client keeps the plain bounded retry.
    /// </summary>
    private async Task<bool> TryFailoverOnConnectionLossAsync(CancellationToken ct)
    {
        if (_source is null || !_source.HasAlternateEndpoints)
            return false;
        if (!await _source.TryFailoverAsync(ct).ConfigureAwait(false))
            return false;
        AdoptCurrentMultiplexer();
        return true;
    }

    /// <summary>
    /// Re-points this connection at the source's current multiplexer, re-subscribes
    /// every doorbell and the topology channel (042 R6.3 / 042-1b), and raises
    /// <see cref="Converged"/> so the engine re-drives pending work on the new master.
    /// </summary>
    private void AdoptCurrentMultiplexer()
    {
        var mux = (ConnectionMultiplexer)_source!.Multiplexer;
        _redis = mux;
        _db = mux.GetDatabase();
        _subscriber = mux.GetSubscriber();
        foreach (var (channel, onMessage) in _doorbells)
        {
            _subscriber.Subscribe(RedisChannel.Literal(channel), (_, value) =>
            {
                try { onMessage((string)value!); } catch { /* doorbells are best-effort */ }
            });
        }
        SubscribeTopology(_subscriber);
        try { Converged?.Invoke(); } catch { /* a re-drive failure surfaces on its own path */ }
    }

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
        _healthTicker?.Dispose();
        await _redis.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// CLIENT SETNAME rejects spaces and newlines, and a rejected name would fail the whole
    /// connection. A display field must never be able to stop a node connecting.
    /// </summary>
    private static string Sanitise(string name)
    {
        Span<char> buf = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        for (var i = 0; i < name.Length; i++)
            buf[i] = char.IsWhiteSpace(name[i]) || char.IsControl(name[i]) ? '_' : name[i];

        return new string(buf);
    }
}
