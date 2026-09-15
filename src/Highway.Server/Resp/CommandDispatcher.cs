using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Resp;

/// <summary>
/// Maps a parsed <c>HW.*</c> command frame to its ported command and runs it (040 T4). This is
/// the transport seam 037 R10 promised: the RESP handler parses bytes into a name + args and
/// hands them here; the command never learns a socket delivered it, so 039's socketless tests
/// stay valid. Replacing Garnet's <c>NewTransactionProc</c> registration, the dispatcher owns
/// the name→command registry and the arity contract that <c>ProtocolConformanceTests</c> and
/// SE.Redis both rely on.
///
/// <para><b>The served subset (037 R6.3, R3.2).</b> Exactly the 18 <c>HW.*</c> names below, with
/// the same arities the Garnet <c>CommandTable</c> carried. An unknown command returns an error
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

    public CommandDispatcher(
        IHighwayStore store,
        StripedLock locks,
        IDoorbell doorbell,
        FlightRecorder recorder,
        HighwayServerOptions options)
    {
        _store = store;
        _locks = locks;
        _doorbell = doorbell;
        _recorder = recorder;
        _options = options;
        _commands = BuildRegistry();
    }

    /// <summary>The served command names, for the "unknown command" error and the protocol doc.</summary>
    public IReadOnlyCollection<string> ServedCommands => (IReadOnlyCollection<string>)_commands.Keys;

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
            // The wire contract (004.1 R2 AC5, ErrorContractTests.WrongArity): arity errors are
            // engine-shaped — Garnet enforced arity before Prepare and answered with Redis's own
            // message, which deliberately does NOT carry the HW_ prefix (clients classify HW_* as
            // Highway validation; a bare ERR is "anything else → permanent"). 037 R1 freezes reply
            // shapes including errors, so the port keeps Garnet's exact form.
            writer.Error($"ERR wrong number of arguments for '{name.ToLowerInvariant()}' command");
            return;
        }

        // Args are everything after the command name. The clock is read once, here.
        var args = new byte[frame.Count - 1][];
        for (var i = 1; i < frame.Count; i++) args[i - 1] = frame[i];

        var ctx = new CommandContext(_store, _locks, _doorbell, _recorder, _options, DateTime.UtcNow.Ticks);
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
    };
}
