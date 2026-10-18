using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Replication;

namespace Highway.Server.Resp;

/// <summary>
/// Maps a parsed <c>HW.*</c> command frame to its ported command and runs it (040 T4). This is
/// the transport seam 037 R10 promised: the RESP handler parses bytes into a name + args and
/// hands them here; the command never learns a socket delivered it, so 039's socketless tests
/// stay valid. Replacing Garnet's <c>NewTransactionProc</c> registration, the dispatcher owns
/// the name→command registry and the arity contract that <c>ProtocolConformanceTests</c> and
/// SE.Redis both rely on.
///
/// <para><b>The served subset (037 R6.3, R3.2, 042 T1).</b> The <c>HW.*</c> names below, with
/// arities the protocol Command Index documents. An unknown command returns an error
/// <b>naming the subset</b>, never a plausible <c>+OK</c>; a known command with the wrong number
/// of arguments returns Garnet's own engine-shaped arity error (no <c>HW_</c> prefix — the
/// 004.1 R2 AC5 classification contract). Arity follows the RESP convention: a positive value is the exact
/// token count (command name included); a negative value <c>-N</c> means "at least <c>N</c>".</para>
///
/// <para>The clock is read <b>once per command</b> here (<c>DateTime.UtcNow.Ticks</c>) and handed
/// to the <see cref="CommandContext"/>, so nothing downstream reads a wall clock inside a batch
/// (037 R5.1). One command instance is built per invocation — the ported commands hold per-call
/// state in fields, exactly as the Garnet procedures did.</para>
/// </summary>
internal sealed class CommandDispatcher
{
    private readonly record struct Entry(int Arity, Func<HighwayCommand> Factory);

    private readonly IReadOnlyDictionary<string, Entry> _commands;
    private readonly IHighwayStore _store;
    private readonly StripedLock _locks;
    private readonly IDoorbell _doorbell;
    private readonly FlightRecorder _recorder;
    private readonly HighwayServerOptions _options;
    private readonly Highway.Server.Storage.Replication.HighwayReplication? _replication;
    private readonly Highway.Server.Storage.Cache.IHighwayCacheStore? _cache;
    private readonly HighwayMetrics? _metrics;
    private readonly SweepThrottle _sweeps = new();   // 061 A2: one per server
    private readonly Storage.Boxes.BoxRuntime _boxes = new();   // 065: catalog cache + time floors, one per server

    /// <summary>The box runtime (065) — exposed for readers outside commands (broker state, inspection).</summary>
    internal Storage.Boxes.BoxRuntime Boxes => _boxes;

    private readonly InstanceRegistry? _instances;   // 072: HW.SESSION's instance descriptions
    private readonly RecentIds<byte> _calls;          // 074: repeated HW.CALL request ids, one per server
    private readonly RecentIds<byte> _sends;          // 074: repeated HW.SEND message ids
    private readonly RecentIds<long> _publishes;      // 074: repeated HW.PUBLISH publish ids → group count
    private readonly RpcOutcomeLog _rpcLog = new();   // 074: RPC requests that ended unanswered, rate-limited

    public CommandDispatcher(
        IHighwayStore store,
        StripedLock locks,
        IDoorbell doorbell,
        FlightRecorder recorder,
        HighwayServerOptions options,
        Highway.Server.Storage.Replication.HighwayReplication? replication = null,
        Highway.Server.Storage.Cache.IHighwayCacheStore? cache = null,
        HighwayMetrics? metrics = null,
        InstanceRegistry? instances = null)
    {
        _instances = instances;
        // A call is remembered until its own deadline (074); the window bounds the longest any can ask for.
        _calls = new RecentIds<byte>(options.RpcDuplicateFilter ? options.MaxRpcTimeout : TimeSpan.Zero);
        _sends = new RecentIds<byte>(options.SendDuplicateWindow);
        _publishes = new RecentIds<long>(options.PublishDuplicateWindow);
        _store = store;
        _locks = locks;
        _doorbell = doorbell;
        _recorder = recorder;
        _options = options;
        _replication = replication;
        _cache = cache;
        _metrics = metrics;
        _commands = BuildRegistry();
        // A new epoch is a new history: cached box ids and floors may describe the old one.
        if (replication is not null) replication.OnEpochChanged += _boxes.Reset;
        // 074: likewise the duplicate memory — an id remembered under the old epoch may name a write the failover
        // lost, or a call the other primary dead-lettered; suppressing its re-send would lose it.
        if (replication is not null) replication.OnEpochChanged += ForgetRecentIds;
    }

    /// <summary>
    /// 042 G7: the session's raw-key write handlers (SET/SETEX/DEL on <c>hw:idem:*</c> /
    /// <c>hw:rep:*</c>) consult this so a non-primary refuses raw writes exactly as it
    /// refuses <c>HW.*</c> verbs. Null replication (in-memory broker) is always writable.
    /// </summary>
    public bool IsWritable => _replication?.IsWritable ?? true;

    /// <summary>The <c>-NOTPRIMARY &lt;endpoint&gt; &lt;epoch&gt;</c> line for the raw surface (no trailing CRLF).</summary>
    public string NotPrimaryLine()
        => _replication is { } r ? $"-NOTPRIMARY {r.RedirectEndpoint()} {r.Epoch}" : "-NOTPRIMARY unknown 0";

    /// <summary>
    /// 042-1c C-T1: the session layer reports authenticated CLIENT sessions (peers — the
    /// replica pullers — classify themselves out via their HW.REPL.* preamble). The count
    /// is the herd: the thing that defines mastership and ends a GOODBYE drain early.
    /// </summary>
    public void NoteClientSessionOpened() => _replication?.ClientSessionOpened();

    public void NoteClientSessionClosed() => _replication?.ClientSessionClosed();

    /// <summary>The served command names, for the "unknown command" error and the protocol doc.</summary>
    public IReadOnlyCollection<string> ServedCommands => (IReadOnlyCollection<string>)_commands.Keys;

    /// <summary>
    /// The registered command surface — every served <c>HW.*</c> name paired with its arity.
    /// This is the authoritative registration table <c>ProtocolConformanceTests</c> checks against
    /// the protocol doc's Command Index, in both directions (feature 041 re-pointed it here off the
    /// deleted Garnet <c>CommandTable</c>). Static: arity is a property of the registry, not of any
    /// running store.
    /// </summary>
    internal static IReadOnlyList<(string Name, int Arity)> RegisteredCommandSurface()
        => [.. BuildRegistry().Select(kv => (kv.Key, kv.Value.Arity))];

    /// <summary>
    /// The prefix of the one raw-key surface the server serves: the RPC reply slot. The client
    /// reads its reply with <c>GET hw:rep:{requestId}</c> and clears it with <c>DEL hw:rep:{requestId}</c>
    /// (see <c>HighwayConnection.GetReplySlotAsync</c>/<c>DeleteReplySlotAsync</c>) — the one place a
    /// Highway client touches a bare key rather than an <c>HW.*</c> command. Garnet served these as an
    /// ordinary key; the RESP server maps them onto the reply-slot store (<see cref="HighwayNames.ReplySlot"/>)
    /// so the client stays unmodified (037 R1 / D2).
    /// </summary>
    public const string CacheKeyPrefix = "hw:cache:";

    /// <summary>True when the broker-local cache is enabled (routes <c>hw:cache:*</c>).</summary>
    public bool CacheEnabled => _cache is not null;

    private static string CacheKey(string wireKey) => wireKey[CacheKeyPrefix.Length..];

    /// <summary>Cache read — live value or null (miss). Null when the cache is disabled.</summary>
    public byte[]? CacheGet(string wireKey)
        => _cache?.Get(CacheKey(wireKey), DateTime.UtcNow.Ticks);

    /// <summary>
    /// Cache write. <paramref name="pxMilliseconds"/> is the caller's expiry (from SET PX/EX or
    /// SETEX); null applies the configured default TTL. Every TTL is clamped to the max
    /// (044 R5). No-op when the cache is disabled.
    /// </summary>
    public void CacheSet(string wireKey, byte[] value, long? pxMilliseconds)
    {
        if (_cache is null) return;
        var now = DateTime.UtcNow.Ticks;
        var ttl = pxMilliseconds is { } px ? TimeSpan.FromMilliseconds(px) : _options.Cache.DefaultTtl;
        if (ttl > _options.Cache.MaxTtl) ttl = _options.Cache.MaxTtl;
        _cache.Set(CacheKey(wireKey), value, now + ttl.Ticks);
    }

    /// <summary>Cache remove. No-op when the cache is disabled.</summary>
    public void CacheRemove(string wireKey) => _cache?.Remove(CacheKey(wireKey));

    /// <summary>Cache entry TTL in ms — Redis PTTL semantics (-2 absent/expired, -1 no expiry, else remaining).</summary>
    public long CacheTtlMs(string wireKey)
    {
        if (_cache is null) return -2;
        var now = DateTime.UtcNow.Ticks;
        var expiry = _cache.GetExpiryTicks(CacheKey(wireKey), now);
        return expiry is null ? -2 : Math.Max(0, (expiry.Value - now) / TimeSpan.TicksPerMillisecond);
    }

    /// <summary>True if <paramref name="name"/> (case-insensitive) is a served <c>HW.*</c> command.</summary>
    public bool IsServed(string name) => _commands.ContainsKey(name.ToUpperInvariant());

    /// <summary>
    /// Dispatches one parsed frame. <paramref name="frame"/> is the whole array: element 0 is the
    /// command name, the rest are arguments. Writes the reply (success or a classified error) to
    /// <paramref name="writer"/>. Never throws for a command-level outcome — errors are data
    /// (037 "errors are data").
    /// </summary>
    public void Dispatch(IReadOnlyList<byte[]> frame, RespWriter writer)
    {
        var name = System.Text.Encoding.ASCII.GetString(frame[0]).ToUpperInvariant();

        if (!_commands.TryGetValue(name, out var entry))
        {
            writer.Error(HighwayErrors.Format(
                HighwayErrors.InvalidArg,
                $"unknown command '{name}'. Served commands: {string.Join(", ", _commands.Keys.Order())}"));
            return;
        }

        if (!ArityMatches(entry.Arity, frame.Count))
        {
            writer.Error($"ERR wrong number of arguments for '{name.ToLowerInvariant()}' command");
            return;
        }

        if (_replication is { } repl)
        {
            repl.Tick();

            // Herd-arrival promotion (042-1c C-T4): a WILLING standby becomes master on
            // its first accepted client verb — the herd connecting IS the promotion
            // trigger. Willingness (own master-link dead ∨ GOODBYE seen) is the no-split
            // guard; probes, handshakes and admin reads never reach here.
            if (!repl.IsWritable && !AllowedWhenNotWritable(name, repl.Role) && repl.IsWillingForHerd()
                && repl.TryPromote("herd-arrival", out _))
            {
                NarrateTopology();
            }

            var refused = repl.IsDraining
                ? !AllowedWhileDraining(name)                                  // GOODBYE quiesce (R12.2)
                : !repl.IsWritable && !AllowedWhenNotWritable(name, repl.Role);
            if (refused)
            {
                writer.NotPrimary(repl.RedirectEndpoint(), repl.Epoch);
                return;
            }
        }

        var args = new byte[frame.Count - 1][];
        for (var i = 1; i < frame.Count; i++) args[i - 1] = frame[i];

        var ctx = new CommandContext(_store, _locks, _doorbell, _recorder, _options, DateTime.UtcNow.Ticks, _replication, _metrics, _sweeps, _boxes, _instances, _calls, _sends, _publishes, _rpcLog);
        var command = entry.Factory();

        // HW.CALL carries the RPC request/latency instruments (051 R1): time the server-side
        // handling and tag the outcome. Every other verb is unmeasured here — its delivery
        // counter is recorded in the command's own AfterCommit, where the outcome is classified.
        if (_metrics is { } metrics && name == "HW.CALL")
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var enqueued = command.Execute(ctx, new CommandInput(args), writer);
            // A suppressed re-send (074) is counted as a duplicate, not as a second request.
            if (command is not HwCallCommand { IsDuplicate: true } and not HwCallCommand { IsFailFast: true })
                metrics.RecordRpcRequest(enqueued, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds);
            return;
        }

        command.Execute(ctx, new CommandInput(args), writer);
    }

    private void ForgetRecentIds()
    {
        _calls.Clear();
        _sends.Clear();
        _publishes.Clear();
    }

    /// <summary>RESP arity: positive = exact token count; negative -N = at least N tokens.</summary>
    private static bool ArityMatches(int arity, int tokenCount)
        => arity >= 0 ? tokenCount == arity : tokenCount >= -arity;

    /// <summary>
    /// The name→command registry — the served subset, with the arities the Garnet
    /// <c>CommandTable</c> carried (039 ported these command bodies verbatim in behaviour).
    /// </summary>
    private static Dictionary<string, Entry> BuildRegistry() => new(StringComparer.Ordinal)
    {
        ["HW.CALL"]        = new(-4, () => new HwCallCommand()),   // 074: optional TIMEOUT <ms>
        ["HW.CANCEL"]      = new(3,  () => new HwCancelCommand()), // 074
        ["HW.REPLY"]       = new(-3, () => new HwReplyCommand()),  // 074: optional TOKEN <token>
        ["HW.RESULT"]      = new(2,  () => new HwResultCommand()),
        ["HW.SEND"]        = new(-4, () => new HwSendCommand()),
        ["HW.PUBLISH"]     = new(-3, () => new HwPublishCommand()),
        ["HW.CLAIM"]       = new(-4, () => new HwClaimCommand()),
        ["HW.ACK"]         = new(2,  () => new HwAckCommand()),
        ["HW.TOUCH"]       = new(2,  () => new HwTouchCommand()),
        ["HW.FAIL"]        = new(4,  () => new HwFailCommand()),
        ["HW.DLQ"]         = new(-3, () => new HwDlqCommand()),
        ["HW.IDEM"]        = new(-4, () => new HwIdemCommand()),
        ["HW.SESSION"]     = new(3,  () => new HwSessionCommand()),
        ["HW.HEARTBEAT"]   = new(-2, () => new HwHeartbeatCommand()),
        ["HW.UNSUBSCRIBE"] = new(3,  () => new HwUnsubscribeCommand()),
        ["HW.JOB"]         = new(-2, () => new HwJobCommand()),
        ["HW.DISCOVER"]    = new(2,  () => new HwDiscoverCommand()),
        ["HW.STATS"]       = new(-1, () => new HwStatsCommand()),
        ["HW.REPLAY"]      = new(-2, () => new HwReplayCommand()),
        ["HW.REPL.HELLO"]  = new(-4, () => new HwReplHelloCommand()),
        ["HW.REPL.PROMOTE"] = new(-1, () => new HwReplPromoteCommand()),
        ["HW.REPL.FENCE"]   = new(-1, () => new HwReplFenceCommand()),
        ["HW.REPL.STATUS"]  = new(1,  () => new HwReplStatusCommand()),
        ["HW.REPL.WITNESS"] = new(1,  () => new HwReplWitnessCommand()),
        ["HW.REPL.JOIN"]    = new(4,  () => new HwReplJoinCommand()),
        ["HW.REPL.GOODBYE"] = new(-1, () => new HwReplGoodbyeCommand()),
    };

    private static bool AllowedWhenNotWritable(string name, ReplicaRole role)
    {
        // HW.REPL.HELLO is the epoch/endpoint gossip channel — a promoted node's
        // announcement must reach a node in ANY role (a Demoted one records where the
        // new primary lives, so its -NOTPRIMARY can redirect there).
        if (name is "HW.STATS" or "HW.DISCOVER" or "HW.REPLAY" or "HW.REPL.HELLO"
            or "HW.REPL.STATUS" or "HW.REPL.PROMOTE" or "HW.REPL.FENCE" or "HW.REPL.WITNESS"
            or "HW.REPL.GOODBYE")
            return true;

        // A fenced primary still ships its data so a replica can catch up: over Crystal's port (076),
        // which Highway's fence does not close.
        return false;
    }

    /// <summary>
    /// The GOODBYE quiesce set (042-1c C-T5 / parent R12.2): completion verbs run so
    /// in-flight work drains — pending replies return, claimed messages ack or record
    /// their failure — while NEW work (calls, sends, publishes, claims, subscriptions,
    /// job admin) is refused so the herd converges on the successor. Replication (Crystal's
    /// port) stays up so standbys catch the tail before the stand-down.
    /// </summary>
    private static bool AllowedWhileDraining(string name)
        => name is "HW.REPLY" or "HW.RESULT" or "HW.ACK" or "HW.FAIL" or "HW.TOUCH" or "HW.IDEM"
            || AllowedWhenNotWritable(name, ReplicaRole.Primary);

    /// <summary>Rings the advisory TOPOLOGY narration with the current roster version (042-1a D4).</summary>
    private void NarrateTopology()
    {
        var version = Storage.Replication.RosterStore.Read(_store).Version;
        _doorbell.Ring("hw:door:topology",
            System.Text.Encoding.UTF8.GetBytes($"TOPOLOGY {version}"));
    }
}
