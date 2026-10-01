namespace Highway.Server.Storage.Layout;

/// <summary>
/// The logical <c>&lt;name&gt;</c> strings the families in <see cref="HighwayKeyspace"/> are keyed by, for
/// the state that is not in a work box (065): the registry, memberships, reply slots and channel sequences.
///
/// <para>070 removed every name that nothing wrote any more — the 2.x list / processing / dead-letter /
/// delayed / schedule / node-set / byte-counter names (their state lives in work boxes since 065) and the
/// pre-025 <c>node:channels</c> index (the node's <see cref="NodeSubs"/> set is its only membership
/// record). A fresh-start format holds no data those names could have addressed.</para>
/// </summary>
internal static class HighwayNames
{
    // --- Box names used in operator-facing messages ------------------------------

    /// <summary>The name a storage-format error reports for a service's work box.</summary>
    public static string ServiceQueue(string service) => $"svc:{service}:q";

    /// <summary>The name a storage-format error reports for a queue's (or group queue's) work box.</summary>
    public static string Queue(string queue) => $"q:{queue}:q";

    // --- Membership sets (family s) -------------------------------------------

    /// <summary>Subscriber groups on a channel.</summary>
    public static string ChannelGroups(string channel) => $"ch:{channel}:groups";

    /// <summary>Nodes backing a subscriber group (025).</summary>
    public static string GroupMembers(string channel, string group) => $"grp:{channel}@{group}:members";

    /// <summary>
    /// The <c>{channel}@{group}</c> entries a node declared (025). Since 070 this is the node's previous
    /// declaration that <c>HW.SESSION</c> diffs against, and the list BYE PURGE works through.
    /// </summary>
    public static string NodeSubs(string nodeId) => $"reg:node:{nodeId}:subs";

    /// <summary>All registered node ids.</summary>
    public const string RegistrationNodeList = "reg:nodes";

    /// <summary>Reverse index: node ids hosting a service.</summary>
    public static string RegistrationService(string service) => $"reg:svc:{service}";

    // --- KV (family k) ---------------------------------------------------------

    /// <summary>RPC reply slot (expiring).</summary>
    public static string ReplySlot(string requestId) => $"rep:{requestId}";

    /// <summary>Node registration record (binary header + catalog).</summary>
    public static string RegistrationNode(string nodeId) => $"reg:node:{nodeId}";

    /// <summary>
    /// A node's liveness timestamp (feature 060): 8 bytes, i64 BE ticks, refreshed by every
    /// <c>HW.HEARTBEAT</c> beat. Kept apart from the registration record so a beat rewrites 8 bytes
    /// instead of the whole catalogue (and ships 8 bytes, not the catalogue, to every standby).
    /// </summary>
    public static string RegistrationSeen(string nodeId) => $"reg:seen:{nodeId}";

    // --- Counters (family n) ---------------------------------------------------

    /// <summary>Per-channel message-ID sequence.</summary>
    public static string ChannelSeq(string channel) => $"ch:{channel}:seq";

    /// <summary>
    /// Per-list sequence allocator for the store's list primitive (<see cref="HighwayKeyspace.ListEntry"/>):
    /// the monotone counter an entry's seq is drawn from, in-batch. Kept with the list primitive, which the
    /// store contract and crash-harness tests exercise.
    /// </summary>
    public static string ListSequence(string listName) => $"seq:{listName}";
}
