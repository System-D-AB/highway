namespace Highway.Server.Storage.Layout;

/// <summary>
/// The logical <c>&lt;name&gt;</c> strings that the four families in
/// <see cref="HighwayKeyspace"/> are keyed by. This is the direct translation of the
/// old Garnet <c>HighwayKeys</c> vocabulary (physical-layout.md §3): each method here
/// names the structure a command wants, and the result is fed to the matching
/// <see cref="HighwayKeyspace"/> builder.
///
/// <para>The <c>hw:</c> prefix from the Garnet keys is gone — the family tag now
/// distinguishes kinds, so the name carries only the concept. The <b>mirror keys are
/// absent</b> (<c>nodelist</c>, <c>grplist</c>, <c>job:index</c>, <c>grp:members</c>,
/// <c>node:subs</c>, <c>node:channels</c>): they existed only to dodge Garnet's
/// <c>Prepare</c> watch conflict, and with no <c>Prepare</c> a set is read directly, so
/// the seven collapse into the sets they mirrored (T3.3).</para>
/// </summary>
internal static class HighwayNames
{
    // --- Lists (family q) ------------------------------------------------------

    /// <summary>RPC request queue for a service. Garnet: <c>hw:svc:{service}:q</c>.</summary>
    public static string ServiceQueue(string service) => $"svc:{service}:q";

    /// <summary>Work queue, or a derived group queue <c>{channel}@{group}</c>. Garnet: <c>hw:q:{queue}:q</c>.</summary>
    public static string Queue(string queue) => $"q:{queue}:q";

    /// <summary>Messages claimed by one node on a queue. Garnet: <c>hw:q:{queue}:proc:{node}</c>.</summary>
    public static string QueueProcessing(string queue, string nodeId) => $"q:{queue}:proc:{nodeId}";

    /// <summary>Messages claimed by one node on a service. Garnet: <c>hw:svc:{service}:proc:{node}</c>.</summary>
    public static string ServiceProcessing(string service, string nodeId) => $"svc:{service}:proc:{nodeId}";

    /// <summary>Dead letters for a queue. Garnet: <c>hw:q:{queue}:dlq</c>. Lives in the <c>dlq</c> column family.</summary>
    public static string QueueDeadLetter(string queue) => $"q:{queue}:dlq";

    /// <summary>Dead letters for a service. Garnet: <c>hw:svc:{service}:dlq</c>. Lives in the <c>dlq</c> column family.</summary>
    public static string ServiceDeadLetter(string service) => $"svc:{service}:dlq";

    // --- Ordered sets (family z) ----------------------------------------------

    /// <summary>Delayed / deferred messages, score = absolute delivery ticks. Garnet: <c>hw:q:{queue}:delayed</c>.</summary>
    public static string QueueDelayed(string queue) => $"q:{queue}:delayed";

    /// <summary>Recurring-job schedules, score = next-fire ticks. Garnet: <c>hw:job:{queue}:schedules</c>.</summary>
    public static string JobSchedules(string queue) => $"job:{queue}:schedules";

    // --- Membership sets (family s) -------------------------------------------

    /// <summary>Nodes holding a processing list for a service. Garnet set: <c>hw:svc:{service}:nodes</c> (mirror <c>:nodelist</c> gone).</summary>
    public static string ServiceNodes(string service) => $"svc:{service}:nodes";

    /// <summary>Nodes that have claimed work on a queue. Garnet set: <c>hw:q:{queue}:nodes</c> (mirror <c>:nodelist</c> gone).</summary>
    public static string QueueNodes(string queue) => $"q:{queue}:nodes";

    /// <summary>Subscriber groups on a channel. Garnet set: <c>hw:ch:{channel}:groups</c> (mirror <c>:grplist</c> gone).</summary>
    public static string ChannelGroups(string channel) => $"ch:{channel}:groups";

    /// <summary>Nodes backing a subscriber group (025). Garnet mirror: <c>hw:grp:members:{ch}@{grp}</c>, now a set.</summary>
    public static string GroupMembers(string channel, string group) => $"grp:{channel}@{group}:members";

    /// <summary>The <c>{channel}@{group}</c> entries a node subscribes through (025). Garnet mirror: <c>hw:reg:node:{node}:subs</c>, now a set.</summary>
    public static string NodeSubs(string nodeId) => $"reg:node:{nodeId}:subs";

    /// <summary>Channels a node subscribes to (017). Garnet mirror: <c>hw:reg:node:{node}:channels</c>, now a set.</summary>
    public static string NodeChannels(string nodeId) => $"reg:node:{nodeId}:channels";

    /// <summary>All registered node ids. Garnet mirror: <c>hw:reg:nodes</c>, now a set.</summary>
    public const string RegistrationNodeList = "reg:nodes";

    /// <summary>Reverse index: node ids hosting a service. Garnet mirror: <c>hw:reg:svc:{service}</c>, now a set.</summary>
    public static string RegistrationService(string service) => $"reg:svc:{service}";

    /// <summary>Queues that have at least one recurring schedule. Garnet mirror: <c>hw:job:index</c>, now a set.</summary>
    public const string JobIndex = "job:index";

    // --- KV (family k) ---------------------------------------------------------

    /// <summary>RPC reply slot; the only expiring key (SetEx). Garnet: <c>hw:rep:{requestId}</c>.</summary>
    public static string ReplySlot(string requestId) => $"rep:{requestId}";

    /// <summary>Node registration record (binary header + catalog). Garnet: <c>hw:reg:node:{node}</c>.</summary>
    public static string RegistrationNode(string nodeId) => $"reg:node:{nodeId}";

    /// <summary>
    /// A node's liveness timestamp (feature 060): 8 bytes, i64 BE ticks, refreshed by every
    /// <c>HW.HEARTBEAT</c> beat. Kept apart from the registration record so a beat rewrites 8 bytes
    /// instead of the whole catalogue (and ships 8 bytes, not the catalogue, to every standby).
    /// </summary>
    public static string RegistrationSeen(string nodeId) => $"reg:seen:{nodeId}";

    // --- Counters (family n) ---------------------------------------------------

    /// <summary>Per-channel message-ID sequence. Garnet: <c>Increment(hw:ch:{channel}:seq)</c>.</summary>
    public static string ChannelSeq(string channel) => $"ch:{channel}:seq";

    /// <summary>Per-queue byte counter (016). Garnet: GET/SET on <c>hw:q:{queue}:bytes</c>; now an atomic counter.</summary>
    public static string QueueBytes(string queue) => $"q:{queue}:bytes";

    /// <summary>
    /// Per-list sequence allocator — the monotone counter a <see cref="HighwayKeyspace.ListEntry"/>
    /// seq is drawn from, in-batch (physical-layout.md §5, the B1 trap). One per list name;
    /// derived from the list's own name so it needs no separate registry.
    /// </summary>
    public static string ListSequence(string listName) => $"seq:{listName}";
}
