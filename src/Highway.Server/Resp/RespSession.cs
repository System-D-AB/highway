using System.Net;
using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;

namespace Highway.Server.Resp;

/// <summary>
/// The per-connection RESP protocol logic (040 T4): the state machine, the handshake subset SE.Redis
/// needs, the auth gate, HW.* dispatch, and subscribe/unsubscribe bookkeeping. It is deliberately
/// <b>transport-free</b> — <see cref="Handle"/> takes a parsed frame and returns the reply frames
/// (and whether to close) — so the state machine is table-testable without a socket, and the Kestrel
/// handler is a thin byte pump on top.
///
/// <para>A frame is always an array of bulk strings (element 0 = command name). Replies are raw
/// RESP bytes. SUBSCRIBE can produce several confirmation frames (one per channel), which is why
/// <see cref="SessionResult"/> carries a list rather than a single reply.</para>
/// </summary>
internal sealed class RespSession
{
    private readonly CommandDispatcher _dispatcher;
    private readonly IConnectionAuthenticator _auth;
    private readonly ISubscriptionSink _subscriptions;
    private readonly EndPoint? _remote;
    private readonly string _connectionId;
    private readonly InstanceRegistry? _observed;

    public RespSession(
        CommandDispatcher dispatcher,
        IConnectionAuthenticator auth,
        ISubscriptionSink subscriptions,
        EndPoint? remote,
        string connectionId = "",
        InstanceRegistry? observed = null)
    {
        _dispatcher = dispatcher;
        _auth = auth;
        _subscriptions = subscriptions;
        _remote = remote;
        _connectionId = connectionId;
        _observed = observed;
        State = _auth.IsPreAuthorized(remote) ? ConnectionState.Authenticated : ConnectionState.Unauthenticated;
    }

    /// <summary>The current state — exposed for the state-machine table tests.</summary>
    public ConnectionState State { get; private set; }

    /// <summary>What one handled frame produces: zero or more reply frames, and whether to close after.</summary>
    public readonly record struct SessionResult(IReadOnlyList<byte[]> Replies, bool Close)
    {
        public static SessionResult One(byte[] reply, bool close = false) => new([reply], close);
        public static SessionResult Many(IReadOnlyList<byte[]> replies) => new(replies, false);
    }

    // ---- session classification (042-1c C-T1) --------------------------------
    // A connection is Unknown until its traffic says what it is: HW.REPL.* (other than
    // the CLIENT handshake) marks a PEER — a replica puller or a joining node; any
    // Highway verb or raw-key operation marks a CLIENT and joins the herd count. The
    // SE.Redis connect preamble (PING/CONFIG/…) classifies nothing.
    private enum SessionKind { Unknown, Client, Peer }
    private SessionKind _kind;

    private void ClassifyIfUnknown(string name, IReadOnlyList<byte[]> frame)
    {
        if (_kind != SessionKind.Unknown || State == ConnectionState.Unauthenticated)
            return;

        if (name.StartsWith("HW.REPL.", StringComparison.Ordinal))
        {
            var isClientHandshake = name == "HW.REPL.HELLO"
                && frame.Count > 1
                && Encoding.ASCII.GetString(frame[1]).Equals("CLIENT", StringComparison.OrdinalIgnoreCase);
            if (!isClientHandshake)
            {
                _kind = SessionKind.Peer;
                return;
            }
            _kind = SessionKind.Client;
            _dispatcher.NoteClientSessionOpened();
            return;
        }

        if (name.StartsWith("HW.", StringComparison.Ordinal)
            || name is "GET" or "SET" or "SETEX" or "PSETEX" or "DEL" or "UNLINK" or "TTL" or "PTTL" or "SUBSCRIBE")
        {
            _kind = SessionKind.Client;
            _dispatcher.NoteClientSessionOpened();
        }
    }

    /// <summary>Connection teardown: a counted client session leaves the herd.</summary>
    public void OnConnectionClosed()
    {
        if (_kind == SessionKind.Client)
            _dispatcher.NoteClientSessionClosed();
        _observed?.Remove(_connectionId);   // 048: the observed address is gone with the connection
        _kind = SessionKind.Peer; // idempotent teardown
    }

    /// <summary>Handles one parsed command frame and returns the reply frames.</summary>
    public SessionResult Handle(IReadOnlyList<byte[]> frame)
    {
        var name = Encoding.ASCII.GetString(frame[0]).ToUpperInvariant();
        ClassifyIfUnknown(name, frame);

        // Pre-auth gate (Redis semantics, kept deliberately): an unauthenticated connection may
        // only AUTH or QUIT. Everything else — PING included, because PING is SE.Redis's connect
        // handshake — answers -NOAUTH, so a credential-less connect FAILS at connect time and the
        // client's 012 mapping (HighwayAuthenticationException, "rejected the supplied
        // credentials… WithPassword") fires exactly as it did under Garnet. 037 R11.3 originally
        // read "AUTH and PING"; amended 2026-09-15 — a pre-auth PONG lets the handshake succeed
        // and turns the refusal into a later, illegible HW.* error, which C6.3 forbids.
        if (State == ConnectionState.Unauthenticated && name is not ("AUTH" or "QUIT"))
            return SessionResult.One(Raw("-NOAUTH Authentication required."));

        // Restricted mode (RESP2): a subscribed connection may only manage subscriptions or PING.
        if (State == ConnectionState.Subscribed
            && name is not ("SUBSCRIBE" or "UNSUBSCRIBE" or "PING" or "QUIT"))
        {
            return SessionResult.One(Err(
                $"only SUBSCRIBE / UNSUBSCRIBE / PING allowed in subscribe mode (RESP2), got '{name}'"));
        }

        return name switch
        {
            "AUTH" => HandleAuth(frame),
            "PING" => HandlePing(frame),
            "QUIT" => new SessionResult([Simple("OK")], Close: true),
            "HELLO" => HandleHello(frame),
            "ECHO" => HandleEcho(frame),
            "SELECT" => SessionResult.One(Simple("OK")),          // single logical DB; accept any index
            "CLIENT" => HandleClient(frame),                     // SETNAME records the observed address (048); else accept
            "COMMAND" => SessionResult.One(EmptyArray()),         // no command catalogue; empty is valid
            "CONFIG" => HandleConfig(frame),
            "INFO" => HandleInfo(frame),
            "SUBSCRIBE" => HandleSubscribe(frame),
            "UNSUBSCRIBE" => HandleUnsubscribe(frame),
            "GET" => HandleGet(frame),                            // reply-slot/idempotency read + SE.Redis tiebreaker probe
            "SET" => HandleSet(frame),                            // idempotency claim (SET … [PX ms] [NX])
            "SETEX" => HandleSetEx(frame, secondsGranularity: true),    // idempotency record — SE.Redis's
            "PSETEX" => HandleSetEx(frame, secondsGranularity: false),  // unconditional expiring write
            "DEL" => HandleDel(frame),                            // reply-slot/idempotency clear
            "UNLINK" => HandleDel(frame),                         // SE.Redis KeyDelete prefers UNLINK; same semantics here
            "PTTL" => HandleTtl(frame, milliseconds: true),       // marker-expiry reads on idempotency keys
            "TTL" => HandleTtl(frame, milliseconds: false),
            _ => HandleHighwayOrUnknown(name, frame),
        };
    }

    // ---- auth ----------------------------------------------------------------

    private SessionResult HandleAuth(IReadOnlyList<byte[]> frame)
    {
        // AUTH <password>  |  AUTH <username> <password>
        if (frame.Count is not (2 or 3))
            return SessionResult.One(Err("wrong number of arguments for 'AUTH'"));

        string? user = frame.Count == 3 ? Encoding.UTF8.GetString(frame[1]) : null;
        var password = Encoding.UTF8.GetString(frame[^1]);

        if (_auth.TryAuthenticate(user, password))
        {
            if (State == ConnectionState.Unauthenticated) State = ConnectionState.Authenticated;
            return SessionResult.One(Simple("OK"));
        }

        // -WRONGPASS is the substring the client classifies into HighwayAuthenticationException.
        return SessionResult.One(Raw("-WRONGPASS invalid username-password pair or user is disabled"));
    }

    // ---- handshake probes ----------------------------------------------------

    private SessionResult HandlePing(IReadOnlyList<byte[]> frame)
        // PING with no arg → +PONG; PING <msg> → bulk echo of msg.
        => frame.Count >= 2
            ? SessionResult.One(BulkOf(frame[1]))
            : SessionResult.One(Simple("PONG"));

    /// <summary>
    /// <c>CLIENT</c> subcommands are accepted; <c>CLIENT SETNAME &lt;node&gt;[@&lt;instance&gt;]</c> additionally
    /// records where the broker sees this connection coming from (048) and which process it belongs to (072).
    /// </summary>
    private SessionResult HandleClient(IReadOnlyList<byte[]> frame)
    {
        if (_observed is not null && frame.Count >= 3
            && Encoding.ASCII.GetString(frame[1]).Equals("SETNAME", StringComparison.OrdinalIgnoreCase))
        {
            var node = Encoding.UTF8.GetString(frame[2]);
            if (_remote is not null && !string.IsNullOrWhiteSpace(node))
                _observed.Record(_connectionId, node, _remote.ToString() ?? "");
        }
        return SessionResult.One(Simple("OK"));
    }

    private SessionResult HandleEcho(IReadOnlyList<byte[]> frame)
        => frame.Count >= 2
            ? SessionResult.One(BulkOf(frame[1]))
            : SessionResult.One(Err("wrong number of arguments for 'ECHO'"));

    private SessionResult HandleHello(IReadOnlyList<byte[]> frame)
    {
        // We speak RESP2 only. If a client asks for RESP3 (HELLO 3), decline so it stays on RESP2 —
        // SE.Redis falls back gracefully. A bare HELLO is answered with a minimal server map.
        if (frame.Count >= 2)
        {
            var proto = Encoding.ASCII.GetString(frame[1]);
            if (proto != "2")
                return SessionResult.One(Err("NOPROTO unsupported protocol version; this server speaks RESP2"));
        }
        // Reached only once authenticated — the pre-auth gate (R11.3 as amended) admits AUTH and
        // QUIT alone. Return a small map-as-array (RESP2).
        return SessionResult.One(HelloMap());
    }

    private SessionResult HandleConfig(IReadOnlyList<byte[]> frame)
    {
        // SE.Redis probes CONFIG GET on connect (e.g. timeout, maxmemory, save). We hold no
        // config; answer GET with an empty array (a valid "no matching keys") and any other
        // subcommand with +OK. Never an error — an error here aborts the client's connect.
        if (frame.Count >= 2 && Encoding.ASCII.GetString(frame[1]).Equals("GET", StringComparison.OrdinalIgnoreCase))
            return SessionResult.One(EmptyArray());
        return SessionResult.One(Simple("OK"));
    }

    private SessionResult HandleInfo(IReadOnlyList<byte[]> frame)
    {
        // A minimal INFO so SE.Redis's server-type detection sees a standalone master. Bulk string.
        var info =
            "# Server\r\nredis_version:7.4.0\r\n# Replication\r\nrole:master\r\nconnected_slaves:0\r\n";
        return SessionResult.One(BulkOf(Encoding.UTF8.GetBytes(info)));
    }

    private SessionResult HandleGet(IReadOnlyList<byte[]> frame)
    {
        if (frame.Count != 2)
            return SessionResult.One(Err("wrong number of arguments for 'GET'"));

        var key = Encoding.UTF8.GetString(frame[1]);

        // 5.0: reply slots and idempotency are HW.RESULT / HW.IDEM; the only raw keys left are the
        // opt-in cache's (a separate store).
        // The broker-local cache read (044). Allowed on a non-master too — a standby's cache
        // is cold/irrelevant, so it simply misses. A miss is the honest answer.
        if (_dispatcher.CacheEnabled && key.StartsWith(CommandDispatcher.CacheKeyPrefix, StringComparison.Ordinal))
        {
            if (!EnsureAuthorized(out var denied)) return SessionResult.One(denied!);
            return SessionResult.One(BulkOrNull(_dispatcher.CacheGet(key)));
        }

        // Any other bare GET is SE.Redis's tiebreaker probe (__Booksleeve_TieBreak). We store no
        // plain keys, so a null bulk is the honest answer and what SE.Redis expects unset.
        return SessionResult.One(NullBulk());
    }

    /// <summary>
    /// <c>SET key value [EX s | PX ms] [NX]</c>, served only for the opt-in cache's keys
    /// (<c>hw:cache:*</c>, 044). Idempotency moved to <c>HW.IDEM</c> in 5.0.
    /// </summary>
    private SessionResult HandleSet(IReadOnlyList<byte[]> frame)
    {
        if (frame.Count < 3)
            return SessionResult.One(Err("wrong number of arguments for 'SET'"));

        var key = Encoding.UTF8.GetString(frame[1]);
        if (!IsCacheKey(key))
            return SessionResult.One(Err(NotServed("SET", key)));

        if (!EnsureAuthorized(out var denied)) return SessionResult.One(denied!);
        if (!EnsureWritable(out var refused)) return SessionResult.One(refused!);

        var value = frame[2];
        long? pxMilliseconds = null;

        for (var i = 3; i < frame.Count; i++)
        {
            var opt = Encoding.ASCII.GetString(frame[i]).ToUpperInvariant();
            switch (opt)
            {
                case "PX" when i + 1 < frame.Count
                    && long.TryParse(Encoding.ASCII.GetString(frame[++i]), out var px):
                    pxMilliseconds = px;
                    break;
                case "EX" when i + 1 < frame.Count
                    && long.TryParse(Encoding.ASCII.GetString(frame[++i]), out var ex):
                    pxMilliseconds = ex * 1000;
                    break;
                case "NX":
                    break;   // no meaning for a cache
                default:
                    return SessionResult.One(Err($"unsupported SET option '{opt}' (served: EX, PX, NX)"));
            }
        }

        _dispatcher.CacheSet(key, value, pxMilliseconds);
        return SessionResult.One(Simple("OK"));
    }

    private bool IsCacheKey(string key)
        => _dispatcher.CacheEnabled && key.StartsWith(CommandDispatcher.CacheKeyPrefix, StringComparison.Ordinal);

    private string NotServed(string verb, string key)
        => _dispatcher.CacheEnabled
            ? $"{verb} is served only for cache keys ('{CommandDispatcher.CacheKeyPrefix}*'); '{key}' is not reachable over the wire"
            : $"{verb} is not served (the cache is disabled); reply slots and idempotency are HW.RESULT / HW.IDEM";

    /// <summary><c>PTTL</c>/<c>TTL</c> for cache keys: Redis semantics (-2 absent/expired, -1 no expiry, else remaining).</summary>
    private SessionResult HandleTtl(IReadOnlyList<byte[]> frame, bool milliseconds)
    {
        if (frame.Count != 2)
            return SessionResult.One(Err($"wrong number of arguments for '{(milliseconds ? "PTTL" : "TTL")}'"));

        var key = Encoding.UTF8.GetString(frame[1]);
        if (!IsCacheKey(key))
            return SessionResult.One(Err(NotServed("TTL", key)));

        if (!EnsureAuthorized(out var denied)) return SessionResult.One(denied!);

        var ms = _dispatcher.CacheTtlMs(key);
        return SessionResult.One(Integer(ms < 0 || milliseconds ? ms : ms / 1000));
    }

    /// <summary><c>SETEX</c>/<c>PSETEX</c>: what SE.Redis emits for <c>StringSetAsync(key, value, expiry)</c>; cache keys only.</summary>
    private SessionResult HandleSetEx(IReadOnlyList<byte[]> frame, bool secondsGranularity)
    {
        if (frame.Count != 4)
            return SessionResult.One(Err($"wrong number of arguments for '{(secondsGranularity ? "SETEX" : "PSETEX")}'"));

        var key = Encoding.UTF8.GetString(frame[1]);
        if (!IsCacheKey(key))
            return SessionResult.One(Err(NotServed("SETEX", key)));

        if (!EnsureAuthorized(out var denied)) return SessionResult.One(denied!);
        if (!EnsureWritable(out var refused)) return SessionResult.One(refused!);

        if (!long.TryParse(Encoding.ASCII.GetString(frame[2]), out var ttl) || ttl <= 0)
            return SessionResult.One(Err("invalid expire time"));

        var px = secondsGranularity ? ttl * 1000 : ttl;
        _dispatcher.CacheSet(key, frame[3], px);
        return SessionResult.One(Simple("OK"));
    }

    private SessionResult HandleDel(IReadOnlyList<byte[]> frame)
    {
        if (frame.Count != 2)
            return SessionResult.One(Err("wrong number of arguments for 'DEL'"));

        var key = Encoding.UTF8.GetString(frame[1]);

        // Cache remove (044).
        if (_dispatcher.CacheEnabled && key.StartsWith(CommandDispatcher.CacheKeyPrefix, StringComparison.Ordinal))
        {
            if (!EnsureAuthorized(out var denied)) return SessionResult.One(denied!);
            if (!EnsureWritable(out var refused)) return SessionResult.One(refused!);
            _dispatcher.CacheRemove(key);
            return SessionResult.One(Integer(1));
        }

        return SessionResult.One(Err(NotServed("DEL", key)));
    }

    // ---- subscriptions -------------------------------------------------------

    private SessionResult HandleSubscribe(IReadOnlyList<byte[]> frame)
    {
        if (frame.Count < 2)
            return SessionResult.One(Err("wrong number of arguments for 'SUBSCRIBE'"));

        if (!EnsureAuthorized(out var denied))
            return SessionResult.One(denied!);

        var replies = new List<byte[]>(frame.Count - 1);
        for (var i = 1; i < frame.Count; i++)
        {
            var channel = Encoding.UTF8.GetString(frame[i]);
            var count = _subscriptions.Subscribe(channel);
            replies.Add(Confirmation("subscribe", frame[i], count));
        }
        State = ConnectionState.Subscribed;
        return SessionResult.Many(replies);
    }

    private SessionResult HandleUnsubscribe(IReadOnlyList<byte[]> frame)
    {
        var replies = new List<byte[]>();
        if (frame.Count < 2)
        {
            // UNSUBSCRIBE with no channel unsubscribes from all — SE.Redis sends this on teardown.
            var remaining = _subscriptions.Count;
            replies.Add(Confirmation("unsubscribe", [], remaining));
        }
        else
        {
            for (var i = 1; i < frame.Count; i++)
            {
                var channel = Encoding.UTF8.GetString(frame[i]);
                var count = _subscriptions.Unsubscribe(channel);
                replies.Add(Confirmation("unsubscribe", frame[i], count));
            }
        }

        if (_subscriptions.Count == 0 && State == ConnectionState.Subscribed)
            State = ConnectionState.Authenticated;

        return SessionResult.Many(replies);
    }

    // ---- HW.* dispatch -------------------------------------------------------

    private SessionResult HandleHighwayOrUnknown(string name, IReadOnlyList<byte[]> frame)
    {
        if (!EnsureAuthorized(out var denied))
            return SessionResult.One(denied!);

        if (!_dispatcher.IsServed(name))
        {
            return SessionResult.One(Err(
                $"unknown command '{name}'. This is a Highway broker; served commands are " +
                $"the HW.* set: {string.Join(", ", _dispatcher.ServedCommands.Order())}"));
        }

        var writer = new RespWriter();
        _dispatcher.Dispatch(frame, writer);
        return SessionResult.One(writer.ToArray());
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// 042 G7: a non-primary refuses raw-key <b>writes</b> exactly as it refuses HW.*
    /// verbs — a client pointed at a replica must not stage local idempotency markers or
    /// destroy reply slots the primary owns. Reads (GET/TTL) stay served.
    /// </summary>
    private bool EnsureWritable(out byte[]? refused)
    {
        if (_dispatcher.IsWritable)
        {
            refused = null;
            return true;
        }

        refused = Raw(_dispatcher.NotPrimaryLine());
        return false;
    }

    /// <summary>Ensures the connection is authenticated; otherwise yields the -NOAUTH reply (037 R11.3).</summary>
    private bool EnsureAuthorized(out byte[]? denied)
    {
        if (State != ConnectionState.Unauthenticated)
        {
            denied = null;
            return true;
        }
        denied = Raw("-NOAUTH Authentication required. Send AUTH <password> (or AUTH <user> <password>) first");
        return false;
    }

    private static byte[] Simple(string s) { var w = new RespWriter(); w.SimpleString(s); return w.ToArray(); }
    private static byte[] EmptyArray() { var w = new RespWriter(); w.EmptyArray(); return w.ToArray(); }
    private static byte[] BulkOf(byte[] value) { var w = new RespWriter(); w.BulkString(value); return w.ToArray(); }
    private static byte[] BulkOrNull(byte[]? value) { var w = new RespWriter(); w.BulkString(value); return w.ToArray(); }
    private static byte[] NullBulk() { var w = new RespWriter(); w.BulkString(null); return w.ToArray(); }
    private static byte[] Integer(long n) { var w = new RespWriter(); w.Integer(n); return w.ToArray(); }
    private static byte[] Confirmation(string kind, byte[] channel, long count)
    { var w = new RespWriter(); w.SubscribeConfirmation(kind, channel, count); return w.ToArray(); }

    private static byte[] Err(string detail) => Raw("-" + HighwayErrors.Format(HighwayErrors.InvalidArg, detail) + "\r\n");
    private static byte[] Raw(string line) => Encoding.UTF8.GetBytes(line.EndsWith("\r\n") ? line : line + "\r\n");

    private static byte[] HelloMap()
    {
        // A RESP2 flat array of key/value bulk strings — enough for SE.Redis to proceed on RESP2.
        var w = new RespWriter();
        w.FieldArray([("server", "highway"), ("version", "1.0.0"), ("proto", "2"), ("role", "master")]);
        return w.ToArray();
    }
}
