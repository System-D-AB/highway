using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// Registry, decommission and lease-sweep machinery ported from
/// <c>HighwayCommandBase.Registry</c>, <c>.Decommission</c> and <c>.LeaseSweep</c> onto
/// <see cref="IHighwayStore"/> (039 T7). This is the <b>mirrors-collapse</b> batch (037 §2,
/// T3.3): every Garnet Main-store "mirror" (a newline-delimited string beside an object-store
/// Set) is gone — the set is read directly with <see cref="IHighwayStore.SetMembers"/>.
///
/// <para>The seven collapsed keys — <c>reg:nodes</c>, <c>grplist</c>, <c>job:index</c>,
/// <c>grp:members</c>, <c>node:subs</c>, <c>node:channels</c>, <c>svc:{s}:nodelist</c> — no
/// longer exist as strings: they are the sets they mirrored. Every former mirror reader
/// (<c>ReadMirrorList</c>, <c>SplitList</c>) becomes a <see cref="IHighwayStore.SetMembers"/>
/// call over the same logical name, keyed with the <c>s</c> family. T7's equivalence tests
/// prove the one surviving copy returns the same answer the mirror did.</para>
///
/// <para>The teardown invariants are unchanged (006): removing a node <b>requeues its
/// unacknowledged RPC work</b> (at-least-once survives a node dying) and <b>leaves its
/// subscriber groups untouched</b> unless a PURGE explicitly retires them (pub/sub outlives
/// the process). Every write stages into the caller's batch; the caller commits once.</para>
/// </summary>
internal static class RegistrySupport
{
    /// <summary>Minimum bytes of a well-formed RPC processing entry (i64 + u16 header).</summary>
    private const int RpcProcessingHeaderSize = 10;

    // -------------------------------------------------------------------------
    // Membership-set reads (the mirror collapse, T3.3)
    // -------------------------------------------------------------------------

    /// <summary>Reads a membership set as strings. Replaces every Garnet <c>ReadMirrorList</c>.</summary>
    public static string[] ReadSet(IHighwayStore store, IStoreSnapshot snap, string name)
        => [.. store.SetMembers(snap, HighwayKeyspace.SetPrefix(name)).Select(m => Encoding.UTF8.GetString(m))];

    /// <summary>Adds a member to a set (idempotent — the store dedupes on the key).</summary>
    public static void SetAdd(IHighwayStore store, IStoreBatch batch, string name, string member)
        => store.SetAdd(batch, HighwayKeyspace.SetPrefix(name), Encoding.UTF8.GetBytes(member));

    /// <summary>Removes a member from a set.</summary>
    public static void SetRemove(IHighwayStore store, IStoreBatch batch, string name, string member)
        => store.SetRemove(batch, HighwayKeyspace.SetPrefix(name), Encoding.UTF8.GetBytes(member));

    // -------------------------------------------------------------------------
    // Node teardown (Registry)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Requeues every unacknowledged request in <paramref name="nodeId"/>'s processing list for
    /// <paramref name="service"/> back to the queue tail (attempt count carried), then clears
    /// the list. Returns the number requeued.
    ///
    /// <para>A prune is a redelivery, so it counts (Envelope.NextAttempt) — otherwise a request
    /// could escape MaxDeliveryAttempts forever via the dead-node path.</para>
    /// </summary>
    public static int RequeueNodeWork(IHighwayStore store, IStoreBatch batch, string service, string nodeId)
    {
        var procName = HighwayNames.ServiceProcessing(service, nodeId);
        var entries = store.ListDrain(batch, HighwayKeyspace.ListPrefix(procName));
        if (entries.Count == 0)
            return 0;

        var requeued = 0;
        foreach (var entry in entries)
        {
            if (entry.Length < RpcProcessingHeaderSize)
                continue; // malformed — re-queuing it would poison the queue

            Envelope.DecodeRpcProcessingEntry(entry, out _, out var requestId, out var payload, out var attempts);
            var revived = Envelope.EncodeRpcEntry(requestId.ToArray(), payload.ToArray(), Envelope.NextAttempt(attempts));
            HwQSendCommand.PushTail(store, batch, HighwayNames.ServiceQueue(service), revived);
            requeued++;
        }

        // ListDrain already emptied the processing list; also drop its seq counter range.
        store.DeleteRange(batch, HighwayKeyspace.ListPrefix(procName));
        return requeued;
    }

    /// <summary>Removes a node from one service's worker set (mirror gone — the set is authoritative).</summary>
    public static void RemoveNodeFromService(IHighwayStore store, IStoreBatch batch, string service, string nodeId)
        => SetRemove(store, batch, HighwayNames.ServiceNodes(service), nodeId);

    /// <summary>Deletes a node's registration record, its liveness key, and drops it from the registry node set.</summary>
    public static void RemoveRegistration(IHighwayStore store, IStoreBatch batch, string nodeId)
    {
        store.Delete(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(nodeId)));
        store.Delete(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationSeen(nodeId)));
        SetRemove(store, batch, HighwayNames.RegistrationNodeList, nodeId);
    }

    /// <summary>
    /// Stages a liveness beat (feature 060): 8 bytes under <c>reg:seen:{node}</c>. The registration
    /// record — and the catalogue inside it — is not rewritten.
    /// </summary>
    public static void TouchSeen(IHighwayStore store, IStoreBatch batch, string nodeId, long nowTicks)
    {
        var value = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(value, nowTicks);
        store.Set(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationSeen(nodeId)), value);
    }

    /// <summary>
    /// A node's effective last-seen time (feature 060): the newer of the registration record's header
    /// (set when the node registered) and its liveness key (refreshed by every beat). Taking the newer of
    /// the two keeps records written before 060 — header only, no liveness key — reading correctly.
    /// </summary>
    public static long SeenTicks(IHighwayStore store, IStoreSnapshot snap, string nodeId, ReadOnlySpan<byte> record)
    {
        NodeRegistration.Decode(record, out var seen, out _);
        var beat = store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationSeen(nodeId)));
        if (beat is { Length: 8 })
            seen = Math.Max(seen, System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(beat));
        return seen;
    }

    /// <summary>Removes a node from one service's discovery index set. Bounded and self-healing (006).</summary>
    public static void RemoveFromServiceIndex(IHighwayStore store, IStoreBatch batch, string service, string nodeId)
        => SetRemove(store, batch, HighwayNames.RegistrationService(service), nodeId);

    // -------------------------------------------------------------------------
    // Group retirement (Decommission)
    // -------------------------------------------------------------------------

    /// <summary>What a retirement destroyed, so an irreversible act leaves a record (017).</summary>
    internal readonly record struct RetirementOutcome(int Groups, long Messages, long Bytes)
    {
        public static RetirementOutcome operator +(RetirementOutcome a, RetirementOutcome b)
            => new(a.Groups + b.Groups, a.Messages + b.Messages, a.Bytes + b.Bytes);
    }

    /// <summary>
    /// Deletes a subscriber group and every key its queue owns, returning what was destroyed.
    /// The derived queue name is <c>{channel}@{group}</c>. Retirement uses
    /// <see cref="IHighwayStore.DeleteRange"/> per structure — a prefix is a contiguous range
    /// on the ordered keyspace, so one range delete replaces Garnet's per-key DELETE loop
    /// (037 §2, C4.6: the range is physically reclaimed at the next compaction).
    ///
    /// <para><b>Deleted, not dead-lettered</b> (017): the messages were addressed to this
    /// subscriber alone and it has declared it will never exist again. Counted before deletion
    /// so the loss is never silent (C4.3).</para>
    /// </summary>
    public static RetirementOutcome RetireGroup(
        IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch, string channel, string group)
    {
        var derived = $"{channel}@{group}";

        // Counted before deletion: afterwards there is nothing left to count.
        var messages = store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue(derived)));
        var bytes = store.ReadByteCounter(snap, HighwayNames.QueueBytes(derived));

        // The whole structure family for a derived group queue.
        store.DeleteRange(batch, HighwayKeyspace.ListPrefix(HighwayNames.Queue(derived)));
        store.DeleteRange(batch, HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed(derived)));
        store.DeleteRange(batch, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter(derived)));
        store.DeleteRange(batch, HighwayKeyspace.SetPrefix(HighwayNames.QueueNodes(derived)));
        store.DeleteRange(batch, HighwayKeyspace.ListPrefix(HighwayNames.QueueProcessing(derived, group)));
        store.DeleteRange(batch, HighwayKeyspace.SetPrefix(HighwayNames.GroupMembers(channel, group)));
        store.Delete(batch, HighwayKeyspace.Counter(HighwayNames.QueueBytes(derived)));
        // Sequence allocators for the drained lists (physical-layout §5) — leave nothing behind.
        store.Delete(batch, HighwayKeyspace.Counter(HighwayNames.ListSequence(HighwayNames.Queue(derived))));
        store.Delete(batch, HighwayKeyspace.Counter(HighwayNames.ListSequence(HighwayNames.Queue(derived)) + ":low"));
        store.Delete(batch, HighwayKeyspace.Counter(HighwayNames.ListSequence(HighwayNames.QueueDeadLetter(derived))));
        store.Delete(batch, HighwayKeyspace.Counter(HighwayNames.ListSequence(HighwayNames.QueueProcessing(derived, group))));

        // Unregister the group itself, or the next publish fans back into a just-deleted queue.
        SetRemove(store, batch, HighwayNames.ChannelGroups(channel), group);

        // Reverse index (node → channels) collapses to a set now (T3.3).
        SetRemove(store, batch, HighwayNames.NodeChannels(group), channel);

        return new RetirementOutcome(1, messages, bytes);
    }

    /// <summary>
    /// The channels a node subscribes to, from the <c>node:channels</c> set (was a mirror).
    /// Used by BYE PURGE to derive the (channel, group) targets it must retire.
    /// </summary>
    public static string[] ReadNodeChannels(IHighwayStore store, IStoreSnapshot snap, string nodeId)
        => ReadSet(store, snap, HighwayNames.NodeChannels(nodeId));
}
