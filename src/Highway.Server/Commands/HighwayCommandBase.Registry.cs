using System.Text;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// Shared registry and node-teardown operations (feature 006).
///
/// <para>These live on the command base rather than in a static helper because
/// they need <c>CreateArgSlice</c>, and they are shared rather than duplicated
/// because a node can be torn down by two different routes — <c>HW.DEQUEUE</c>
/// discovering it is stale, and <c>HW.HEARTBEAT &lt;node&gt; BYE</c> announcing
/// its departure. Two implementations of the same teardown would drift, and the
/// half that drifted would silently violate one of the two invariants below.</para>
///
/// <para><b>Teardown invariants.</b> Removing a node MUST:</para>
/// <list type="bullet">
///   <item>requeue its unacknowledged RPC requests — at-least-once has to survive
///         a node dying, not only a lease expiring;</item>
///   <item>leave its subscriber groups <b>completely untouched</b> — pub/sub
///         messages are addressed to a group and deliberately outlive the
///         process, so a restarting node resumes its pending messages. Deleting
///         them here would silently downgrade durable pub/sub to fire-and-forget
///         for any node that outlives its expiry window.</item>
/// </list>
/// </summary>
internal abstract partial class HighwayCommandBase
{
    /// <summary>Minimum bytes of a well-formed RPC processing entry (i64 + u16 header).</summary>
    private const int RpcProcessingHeaderSize = 10;

    /// <summary>
    /// Requeues every unacknowledged request in <paramref name="nodeId"/>'s
    /// processing list for <paramref name="service"/> back to the queue tail,
    /// then clears the list.
    /// </summary>
    /// <returns>Number of requests returned to the queue.</returns>
    protected int RequeueNodeWork<TGarnetApi>(TGarnetApi api, string service, string nodeId)
        where TGarnetApi : IGarnetApi
    {
        var procKey  = CreateArgSlice(HighwayKeys.ServiceProcessing(service, nodeId));
        var queueKey = CreateArgSlice(HighwayKeys.ServiceQueue(service));

        var status = api.ListLeftPop(procKey, int.MaxValue, out var entries);
        if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
            return 0;

        var requeued = 0;
        foreach (var entry in entries)
        {
            var span = entry.ReadOnlySpan;
            if (span.Length < RpcProcessingHeaderSize)
                continue; // malformed — cannot be recovered, and re-queuing it would poison the queue

            Envelope.DecodeRpcProcessingEntry(span, out _, out var requestId, out var payload, out var attempts);

            // A prune is a redelivery like any other, so it counts. Not counting it here
            // would let a request escape MaxDeliveryAttempts indefinitely by always being
            // recovered through the dead-node path rather than the lease sweep.
            api.ListRightPush(
                queueKey,
                CreateArgSlice(Envelope.EncodeRpcEntry(requestId, payload, Envelope.NextAttempt(attempts))),
                out _);
            requeued++;
        }

        api.DELETE(procKey);
        return requeued;
    }

    /// <summary>
    /// Removes a node from one service's worker set and its main-store mirror,
    /// so <c>HW.DEQUEUE</c> stops locking and sweeping that node's list.
    /// </summary>
    protected void RemoveNodeFromService<TGarnetApi>(TGarnetApi api, string service, string nodeId)
        where TGarnetApi : IGarnetApi
    {
        api.SetRemove(
            CreateArgSlice(HighwayKeys.ServiceNodes(service)),
            CreateArgSlice(Encoding.UTF8.GetBytes(nodeId)),
            out _);

        RemoveFromMirrorList(api, HighwayKeys.ServiceNodeList(service), nodeId);
    }

    /// <summary>
    /// Deletes a node's registration record and drops it from the registry node
    /// list. Callers must have locked both keys.
    /// </summary>
    protected void RemoveRegistration<TGarnetApi>(TGarnetApi api, string nodeId)
        where TGarnetApi : IGarnetApi
    {
        api.DELETE(CreateArgSlice(HighwayKeys.RegistrationNode(nodeId)));
        RemoveFromMirrorList(api, HighwayKeys.RegistrationNodeList, nodeId);
    }

    /// <summary>
    /// Removes a node from one service's discovery index. Bounded on purpose:
    /// a caller can only lock the index keys for services it knows about, so
    /// each service's index is cleaned by traffic on that service. A dangling
    /// entry for a service not cleaned yet is harmless — <c>HW.DISCOVER</c>
    /// reads each candidate's registration record and skips missing ones — and
    /// it is rebuilt correctly if the node re-registers.
    /// </summary>
    protected void RemoveFromServiceIndex<TGarnetApi>(TGarnetApi api, string service, string nodeId)
        where TGarnetApi : IGarnetApi
        => RemoveFromMirrorList(api, HighwayKeys.RegistrationService(service), nodeId);

    /// <summary>Adds a value to a newline-delimited main-store list if absent.</summary>
    protected void AddToMirrorList<TGarnetApi>(TGarnetApi api, string key, string value)
        where TGarnetApi : IGarnetApi
    {
        var slice = CreateArgSlice(key);
        api.GET(slice, out PinnedSpanByte current);

        var items = SplitList(current);
        if (Array.IndexOf(items, value) >= 0)
            return;

        var updated = items.Length == 0 ? value : string.Join('\n', items) + "\n" + value;
        api.SET(slice, CreateArgSlice(updated));
    }

    /// <summary>Removes a value from a newline-delimited main-store list, deleting the key when empty.</summary>
    protected void RemoveFromMirrorList<TGarnetApi>(TGarnetApi api, string key, string value)
        where TGarnetApi : IGarnetApi
    {
        var slice = CreateArgSlice(key);
        api.GET(slice, out PinnedSpanByte current);
        if (current.Length == 0)
            return;

        var remaining = SplitList(current).Where(x => x != value).ToArray();
        if (remaining.Length > 0)
            api.SET(slice, CreateArgSlice(string.Join('\n', remaining)));
        else
            api.DELETE(slice);
    }

    /// <summary>Reads a newline-delimited main-store list; empty when the key is absent.</summary>
    protected string[] ReadMirrorList<TGarnetApi>(TGarnetApi api, string key)
        where TGarnetApi : IGarnetApi
    {
        api.GET(CreateArgSlice(key), out PinnedSpanByte value);
        return SplitList(value);
    }

    /// <summary>Splits a newline-delimited list value. Safe because identifiers cannot contain control characters.</summary>
    internal static string[] SplitList(PinnedSpanByte value)
        => value.Length > 0
            ? Encoding.UTF8.GetString(value.ReadOnlySpan).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];
    // -------------------------------------------------------------------------
    // Byte accounting (016)
    //
    // A running counter per queue, held in the MAIN store beside the queue and
    // updated inside the same transaction that pushes or pops the entry.
    //
    // Why a counter and not a measurement: asking Garnet for a structure's size
    // on every enqueue is O(n); this is O(1) under a lock we already hold, which
    // is what keeps R2.4 (no measurable cost on the write path) true.
    //
    // The price is that a counter trusts every writer. That is why the drift test
    // exists — it recomputes the real size and compares, because the paths that
    // forget to maintain a counter are exactly the ones nobody thought about.
    //
    // Scope: the LIVE QUEUE only. Processing lists are bounded by concurrency x
    // nodes and dead-letter lists carry their own cap, so neither can grow without
    // limit; the live queue is the structure that does when consumers are absent.
    // -------------------------------------------------------------------------

    /// <summary>Reads a queue's byte counter. Absent or unparsable reads as zero.</summary>
    protected long ReadByteCounter<TGarnetApi>(TGarnetApi api, string counterKey)
        where TGarnetApi : IGarnetApi
    {
        api.GET(CreateArgSlice(counterKey), out PinnedSpanByte current);

        return current.Length > 0
            && long.TryParse(
                Encoding.UTF8.GetString(current.ReadOnlySpan),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            ? value
            : 0;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to a queue's byte counter, deleting the key when it
    /// reaches zero so an idle queue leaves nothing behind.
    /// </summary>
    protected void AdjustByteCounter<TGarnetApi>(TGarnetApi api, string counterKey, long delta)
        where TGarnetApi : IGarnetApi
    {
        var slice = CreateArgSlice(counterKey);
        var updated = ReadByteCounter(api, counterKey) + delta;

        // Clamped at zero: a counter that goes negative would silently grant extra headroom,
        // which is the failure mode hardest to notice and worst to debug.
        if (updated <= 0)
            api.DELETE(slice);
        else
            api.SET(slice, CreateArgSlice(
                updated.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

}
