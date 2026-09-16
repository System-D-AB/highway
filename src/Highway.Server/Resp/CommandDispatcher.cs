using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Rocks;

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
    private readonly Highway.Server.Storage.Rocks.ReplicationFeeder? _replication;

    public CommandDispatcher(
        IHighwayStore store,
        StripedLock locks,
        IDoorbell doorbell,
        FlightRecorder recorder,
        HighwayServerOptions options,
        Highway.Server.Storage.Rocks.ReplicationFeeder? replication = null)
    {
        _store = store;
        _locks = locks;
        _doorbell = doorbell;
        _recorder = recorder;
        _options = options;
        _replication = replication;
        _commands = BuildRegistry();
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
    public const string ReplyKeyPrefix = "hw:rep:";

    /// <summary>
    /// Reads a reply slot by its wire key (<c>hw:rep:{requestId}</c>), honouring expiry
    /// (<see cref="IHighwayStore.GetLive"/>). Returns null when absent or expired. The clock is read
    /// once, here — the store never reads a wall clock (037 R5.1).
    /// </summary>
    public byte[]? ReadReplySlot(string wireKey)
    {
        var requestId = wireKey[ReplyKeyPrefix.Length..];
        using var snap = _store.Snapshot();
        return _store.GetLive(snap, HighwayKeyspace.Kv(HighwayNames.ReplySlot(requestId)), DateTime.UtcNow.Ticks);
    }

    /// <summary>Deletes a reply slot by its wire key (<c>hw:rep:{requestId}</c>) — the caller collecting its reply.</summary>
    public void DeleteReplySlot(string wireKey)
    {
        var requestId = wireKey[ReplyKeyPrefix.Length..];
        using var batch = _store.NewBatch();
        _store.Delete(batch, HighwayKeyspace.Kv(HighwayNames.ReplySlot(requestId)));
        batch.Commit();
    }

    /// <summary>
    /// The client's idempotency keys (<c>hw:idem:{name}:{id}</c> — see
    /// <c>HighwayConnection.IdempotencyKey</c>): the second and last place a Highway client
    /// touches bare keys (<c>SET … PX … NX</c> to claim, <c>GET</c> to read a prior outcome,
    /// <c>SET … PX</c> to record it, <c>DEL</c> to release). Garnet served these as ordinary
    /// keys; the RESP server maps them onto the KV family so the client stays unmodified
    /// (037 R1 / D2) — found by the 040 fixture swap's idempotency tests.
    /// </summary>
    public const string IdempotencyKeyPrefix = "hw:idem:";

    private static byte[] IdemStoreKey(string wireKey)
        => HighwayKeyspace.Kv(wireKey[3..]); // strip "hw:" — the same convention every name follows

    /// <summary>
    /// <c>SET</c> on an idempotency key. <paramref name="pxMilliseconds"/> maps to the KV
    /// expiry (absolute, computed here — the store never reads a clock, 037 R5.1);
    /// <paramref name="notExists"/> is the NX claim. Returns false when NX found a live value.
    /// The per-key stripe serializes concurrent claims for the same id.
    /// </summary>
    public bool WriteIdempotencyKey(string wireKey, byte[] value, long? pxMilliseconds, bool notExists)
    {
        var now = DateTime.UtcNow.Ticks;
        using var _ = _locks.Lock(wireKey);

        if (notExists)
        {
            using var snap = _store.Snapshot();
            if (_store.GetLive(snap, IdemStoreKey(wireKey), now) is not null) return false;
        }

        using var batch = _store.NewBatch();
        if (pxMilliseconds is { } px)
            _store.SetEx(batch, IdemStoreKey(wireKey), value, now + px * TimeSpan.TicksPerMillisecond);
        else
            _store.Set(batch, IdemStoreKey(wireKey), value);
        batch.Commit();
        return true;
    }

    /// <summary>Reads an idempotency key, honouring expiry. Null when absent or expired.</summary>
    public byte[]? ReadIdempotencyKey(string wireKey)
    {
        using var snap = _store.Snapshot();
        return _store.GetLive(snap, IdemStoreKey(wireKey), DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Remaining time-to-live of an idempotency key in milliseconds: -2 when absent or
    /// expired, -1 when it has no expiry (Redis PTTL semantics). Reads the expiry out of
    /// the KV framing; serves the suite's marker-expiry assertions.
    /// </summary>
    public long ReadIdempotencyTtlMs(string wireKey)
    {
        var now = DateTime.UtcNow.Ticks;
        using var snap = _store.Snapshot();

        // Raw read + the shared framing (no seam change): the framed header carries the
        // absolute expiry; a plain value has none; an expired or absent key is gone.
        var framed = _store.Get(snap, IdemStoreKey(wireKey));
        if (framed is null || ExpiryFraming.Unwrap(framed, now) is null) return -2;
        var expiresAt = ExpiryFraming.TryReadExpiry(framed);
        return expiresAt is null ? -1 : Math.Max(0, (expiresAt.Value - now) / TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Deletes an idempotency key — the client releasing a claim after a failure.</summary>
    public void DeleteIdempotencyKey(string wireKey)
    {
        using var batch = _store.NewBatch();
        _store.Delete(batch, IdemStoreKey(wireKey));
        batch.Commit();
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

        var ctx = new CommandContext(_store, _locks, _doorbell, _recorder, _options, DateTime.UtcNow.Ticks, _replication);
        var command = entry.Factory();
        command.Execute(ctx, new CommandInput(args), writer);
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
        ["HW.CALL"]        = new(4,  () => new HwCallCommand()),
        ["HW.REPLY"]       = new(3,  () => new HwReplyCommand()),
        ["HW.DEQUEUE"]     = new(3,  () => new HwDequeueCommand()),
        ["HW.ACK"]         = new(4,  () => new HwAckCommand()),
        ["HW.SUBSCRIBE"]   = new(-3, () => new HwSubscribeCommand()),
        ["HW.UNSUBSCRIBE"] = new(3,  () => new HwUnsubscribeCommand()),
        ["HW.PUBLISH"]     = new(-3, () => new HwPublishCommand()),
        ["HW.HEARTBEAT"]   = new(-2, () => new HwHeartbeatCommand()),
        ["HW.DISCOVER"]    = new(2,  () => new HwDiscoverCommand()),
        ["HW.STATS"]       = new(-1, () => new HwStatsCommand()),
        ["HW.REPLAY"]      = new(-2, () => new HwReplayCommand()),
        ["HW.DLQ"]         = new(-3, () => new HwDlqCommand()),
        ["HW.QSEND"]       = new(-4, () => new HwQSendCommand()),
        ["HW.QCLAIM"]      = new(3,  () => new HwQClaimCommand()),
        ["HW.QACK"]        = new(4,  () => new HwQAckCommand()),
        ["HW.FAIL"]        = new(7,  () => new HwFailCommand()),
        ["HW.TOUCH"]       = new(5,  () => new HwTouchCommand()),
        ["HW.JOB"]         = new(-2, () => new HwJobCommand()),
        ["HW.REPL.HELLO"]  = new(-4, () => new HwReplHelloCommand()),
        ["HW.REPL.PULL"]   = new(3,  () => new HwReplPullCommand()),
        ["HW.REPL.ACK"]    = new(3,  () => new HwReplAckCommand()),
        ["HW.REPL.SNAPSHOT"]= new(-2, () => new HwReplSnapshotCommand()),
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

        // A fenced primary still ships WAL so a replica can catch up.
        if (role == ReplicaRole.Fenced && name is "HW.REPL.PULL" or "HW.REPL.ACK" or "HW.REPL.SNAPSHOT")
            return true;

        return false;
    }

    /// <summary>
    /// The GOODBYE quiesce set (042-1c C-T5 / parent R12.2): completion verbs run so
    /// in-flight work drains — pending replies return, claimed messages ack or record
    /// their failure — while NEW work (calls, sends, publishes, claims, subscriptions,
    /// job admin) is refused so the herd converges on the successor. WAL shipping stays
    /// up so standbys catch the tail before the stand-down.
    /// </summary>
    private static bool AllowedWhileDraining(string name)
        => name is "HW.REPLY" or "HW.ACK" or "HW.QACK" or "HW.FAIL" or "HW.TOUCH"
            or "HW.REPL.PULL" or "HW.REPL.ACK" or "HW.REPL.SNAPSHOT"
            || AllowedWhenNotWritable(name, ReplicaRole.Primary);

    /// <summary>Rings the advisory TOPOLOGY narration with the current roster version (042-1a D4).</summary>
    private void NarrateTopology()
    {
        var version = Storage.Rocks.RosterStore.Read(_store).Version;
        _doorbell.Ring("hw:door:topology",
            System.Text.Encoding.UTF8.GetBytes($"TOPOLOGY {version}"));
    }
}
