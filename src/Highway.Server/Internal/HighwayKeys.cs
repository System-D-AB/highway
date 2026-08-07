using System.Text;

namespace Highway.Server.Internal;

/// <summary>
/// Static helpers that produce the canonical Redis key and doorbell-channel
/// strings for every Highway data structure.
///
/// All keys are under the <c>hw:</c> namespace.  UTF-8 byte[] overloads are
/// provided for Garnet APIs that work with <c>PinnedSpanByte</c> / byte arrays.
/// </summary>
internal static class HighwayKeys
{
    // -------------------------------------------------------------------------
    // RPC keys
    // -------------------------------------------------------------------------

    /// <summary>Pending RPC request queue for a service.  hw:svc:{service}:q</summary>
    public static string ServiceQueue(string service) => $"hw:svc:{service}:q";

    /// <summary>Processing list owned by one node.  hw:svc:{service}:proc:{nodeId}</summary>
    public static string ServiceProcessing(string service, string nodeId) =>
        $"hw:svc:{service}:proc:{nodeId}";

    /// <summary>Set of node IDs that currently hold a processing list.  hw:svc:{service}:nodes</summary>
    public static string ServiceNodes(string service) => $"hw:svc:{service}:nodes";

    /// <summary>
    /// Main-store string holding node IDs (newline-delimited) for a service.
    /// Used by HW.DEQUEUE to avoid object-store reads in Prepare that trigger
    /// watch conflicts. Maintained alongside the set by DEQUEUE itself.
    /// hw:svc:{service}:nodelist
    /// </summary>
    public static string ServiceNodeList(string service) => $"hw:svc:{service}:nodelist";

    /// <summary>Reply slot for a request.  hw:rep:{requestId}</summary>
    public static string ReplySlot(string requestId) => $"hw:rep:{requestId}";

    // -------------------------------------------------------------------------
    // Pub/Sub keys
    // -------------------------------------------------------------------------

    /// <summary>Set of subscriber group names for a channel.  hw:ch:{channel}:groups</summary>
    public static string ChannelGroups(string channel) => $"hw:ch:{channel}:groups";

    /// <summary>
    /// Main-store string holding group names (newline-delimited) for a channel.
    /// Used by HW.PUBLISH to avoid object-store reads in Prepare that trigger
    /// watch conflicts. Maintained alongside the set by SUBSCRIBE/UNSUBSCRIBE.
    /// hw:ch:{channel}:grplist
    /// </summary>
    public static string ChannelGroupList(string channel) => $"hw:ch:{channel}:grplist";

    /// <summary>Per-channel message-ID sequence counter.  hw:ch:{channel}:seq</summary>
    public static string ChannelSeq(string channel) => $"hw:ch:{channel}:seq";

    /// <summary>Backlog of messages published with no active subscriber groups.  hw:ch:{channel}:backlog</summary>
    public static string ChannelBacklog(string channel) => $"hw:ch:{channel}:backlog";

    /// <summary>Pending message queue for a specific subscriber group.  hw:ch:{channel}:grp:{group}:q</summary>
    public static string GroupQueue(string channel, string group) =>
        $"hw:ch:{channel}:grp:{group}:q";

    /// <summary>Processing list for a subscriber group.  hw:ch:{channel}:grp:{group}:proc</summary>
    public static string GroupProcessing(string channel, string group) =>
        $"hw:ch:{channel}:grp:{group}:proc";

    // -------------------------------------------------------------------------
    // Registry keys (feature 006)
    //
    // All three are MAIN-STORE strings, not object-store sets. This is mandatory,
    // not stylistic: reading an object-store set in Prepare goes through
    // GarnetWatchApi, which registers a WATCH on the key; the later exclusive
    // lock on that same key then fails watch-version validation and aborts the
    // transaction. Main-store GET keeps Prepare watch-free on keys we then lock.
    // Same constraint that produced the 004 nodelist/grplist mirror keys.
    //
    // Newline-delimited membership is safe because Identifier.IsValid rejects
    // every C0 control character, so no identifier can contain the delimiter.
    // -------------------------------------------------------------------------

    /// <summary>Registration record for one node.  hw:reg:node:{nodeId}</summary>
    public static string RegistrationNode(string nodeId) => $"hw:reg:node:{nodeId}";

    /// <summary>Newline-delimited list of all registered node IDs.  hw:reg:nodes</summary>
    public const string RegistrationNodeList = "hw:reg:nodes";

    /// <summary>
    /// Reverse index: newline-delimited node IDs hosting a service.
    /// Maintained by the HW.HEARTBEAT registration form so HW.DISCOVER is a
    /// lookup rather than a scan over every node's catalog.
    /// hw:reg:svc:{service}
    /// </summary>
    public static string RegistrationService(string service) => $"hw:reg:svc:{service}";

    // -------------------------------------------------------------------------
    // Doorbell channels (RESP pub/sub, rung via SubscribeBroker.PublishNow)
    // -------------------------------------------------------------------------

    /// <summary>Rung by HW.CALL after enqueue.  hw:door:svc:{service}</summary>
    public static string ServiceDoorbell(string service) => $"hw:door:svc:{service}";

    /// <summary>Rung by HW.REPLY after writing the reply slot.  hw:door:rep</summary>
    public const string ReplyDoorbell = "hw:door:rep";

    /// <summary>Rung by HW.PUBLISH per group.  hw:door:ch:{channel}:grp:{group}</summary>
    public static string GroupDoorbell(string channel, string group) =>
        $"hw:door:ch:{channel}:grp:{group}";

    // -------------------------------------------------------------------------
    // UTF-8 byte[] overloads  (Garnet APIs operate on byte arrays / PinnedSpanByte)
    // -------------------------------------------------------------------------

    public static byte[] ServiceQueueBytes(string service) =>
        Encoding.UTF8.GetBytes(ServiceQueue(service));

    public static byte[] ServiceProcessingBytes(string service, string nodeId) =>
        Encoding.UTF8.GetBytes(ServiceProcessing(service, nodeId));

    public static byte[] ServiceNodesBytes(string service) =>
        Encoding.UTF8.GetBytes(ServiceNodes(service));

    public static byte[] ReplySlotBytes(string requestId) =>
        Encoding.UTF8.GetBytes(ReplySlot(requestId));

    public static byte[] ChannelGroupsBytes(string channel) =>
        Encoding.UTF8.GetBytes(ChannelGroups(channel));

    public static byte[] ChannelSeqBytes(string channel) =>
        Encoding.UTF8.GetBytes(ChannelSeq(channel));

    public static byte[] ChannelBacklogBytes(string channel) =>
        Encoding.UTF8.GetBytes(ChannelBacklog(channel));

    public static byte[] GroupQueueBytes(string channel, string group) =>
        Encoding.UTF8.GetBytes(GroupQueue(channel, group));

    public static byte[] GroupProcessingBytes(string channel, string group) =>
        Encoding.UTF8.GetBytes(GroupProcessing(channel, group));

    public static byte[] ServiceDoorbellBytes(string service) =>
        Encoding.UTF8.GetBytes(ServiceDoorbell(service));

    public static readonly byte[] ReplyDoorbellBytes =
        Encoding.UTF8.GetBytes(ReplyDoorbell);

    public static byte[] GroupDoorbellBytes(string channel, string group) =>
        Encoding.UTF8.GetBytes(GroupDoorbell(channel, group));

    public static byte[] RegistrationNodeBytes(string nodeId) =>
        Encoding.UTF8.GetBytes(RegistrationNode(nodeId));

    public static readonly byte[] RegistrationNodeListBytes =
        Encoding.UTF8.GetBytes(RegistrationNodeList);

    public static byte[] RegistrationServiceBytes(string service) =>
        Encoding.UTF8.GetBytes(RegistrationService(service));
}
