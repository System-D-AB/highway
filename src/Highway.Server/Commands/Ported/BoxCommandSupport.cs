using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Highway.Server.Commands.Runtime;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// What the 5.0 work commands share (065 / 068): target grammar, claim tokens, and opening a box under its
/// lock. Every work operation resolves its box through the catalog <b>before</b> taking the box's lock
/// (catalog creation takes its own lock; the stripes are not re-entrant), then holds that one lock for the
/// whole operation.
/// </summary>
internal static class BoxCommandSupport
{
    /// <summary><c>SVC</c> → a service's request queue; <c>Q</c> → a queue or a group queue <c>{channel}@{group}</c>.</summary>
    public static bool TryParseTarget(ReadOnlySpan<byte> word, out BoxKeys.Kind kind)
    {
        var text = Encoding.ASCII.GetString(word).ToUpperInvariant();
        kind = text == "SVC" ? BoxKeys.Kind.Service : BoxKeys.Kind.Queue;
        return text is "SVC" or "Q";
    }

    /// <summary>A claim token: <c>{box}.{seq}</c>, opaque to clients. Sequences are never reused in a box.</summary>
    public static string Token(uint box, ulong seq)
        => box.ToString(CultureInfo.InvariantCulture) + "." + seq.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseToken(ReadOnlySpan<byte> raw, out uint box, out ulong seq)
    {
        box = 0;
        seq = 0;
        var dot = raw.IndexOf((byte)'.');
        return dot > 0
            && uint.TryParse(raw[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out box)
            && ulong.TryParse(raw[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out seq)
            && box >= BoxKeys.FirstWorkBox;
    }

    /// <summary>The box for a work target, created on first use (a send to an unknown queue is valid).</summary>
    public static uint Ensure(CommandContext ctx, BoxKeys.Kind kind, string name)
        => Catalog.Ensure(ctx.Store, ctx.Boxes, ctx.Locks, kind, name, ctx.NowTicks);

    /// <summary>The box for a work target, or null when nothing was ever sent to or claimed from it.</summary>
    public static uint? Find(CommandContext ctx, BoxKeys.Kind kind, string name)
    {
        using var batch = ctx.Store.NewBatch();
        return Catalog.Find(ctx.Store, batch, ctx.Boxes, kind, name);
    }

    /// <summary>A box's kind and name (for a token command), or null when the box is not a live work box.</summary>
    public static (BoxKeys.Kind Kind, string Name)? Describe(CommandContext ctx, uint box)
    {
        if (ctx.Boxes.TryGetMeta(box, out var cached)) return cached;
        using var batch = ctx.Store.NewBatch();
        if (Catalog.ReadMeta(ctx.Store, batch, box) is not { State: "active" } meta) return null;
        ctx.Boxes.RememberMeta(box, meta.Kind, meta.Name);
        return (meta.Kind, meta.Name);
    }

    public static WorkBox Open(CommandContext ctx, uint box, BoxKeys.Kind kind, IStoreBatch? sharedBatch = null)
        => new(ctx.Store, ctx.Boxes, box, tracksBytes: kind == BoxKeys.Kind.Queue, sharedBatch,
            rawEntries: kind == BoxKeys.Kind.DeadFeed);

    public static IDisposable Lock(CommandContext ctx, uint box) => ctx.Locks.Lock(WorkBox.LockName(box));

    // =====================================================================================
    // Resolve-then-lock, validated (070 second review, R2-1)
    // =====================================================================================

    private const int MaxLiveAttempts = 16;

    /// <summary>
    /// Only subscriber-group queues (<c>{channel}@{group}</c>) are ever retired, so only their box id can
    /// change under a name. Services and plain queues keep their box for life and skip every check below.
    /// </summary>
    public static bool CanRetire(BoxKeys.Kind kind, string name) => kind == BoxKeys.Kind.Queue && name.Contains('@');

    /// <summary>The box the store names for (kind, name) right now — a direct read that bypasses the id cache.</summary>
    public static uint? LiveBox(CommandContext ctx, BoxKeys.Kind kind, string name)
    {
        using var batch = ctx.Store.NewBatch();
        var raw = ctx.Store.Read(batch, BoxKeys.NameIndexKey(kind, name));
        return raw is { Length: 4 } ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw) : null;
    }

    /// <summary>
    /// Resolves the box for (kind, name), takes its lock, and confirms <b>under the lock</b> that the name
    /// still names that box. A box id is resolved before its lock is taken (the catalog has its own lock;
    /// the stripes are not re-entrant), so a group retired — and possibly re-created — in between would
    /// otherwise be written into after its range was deleted: the message acknowledged and lost. When the
    /// name moved, the stale id is forgotten and resolution starts over. <paramref name="create"/> = Ensure
    /// (a send or claim to an unknown queue is valid); otherwise <paramref name="box"/> is null when the
    /// name has no box.
    /// </summary>
    public static IDisposable LockLive(CommandContext ctx, BoxKeys.Kind kind, string name, bool create, out uint? box)
    {
        for (var attempt = 1; ; attempt++)
        {
            box = create ? Ensure(ctx, kind, name) : Find(ctx, kind, name);
            if (box is not { } id) return NoLock.Instance;
            var lease = Lock(ctx, id);
            if (!CanRetire(kind, name) || LiveBox(ctx, kind, name) == id)
                return lease;
            lease.Dispose();
            ctx.Boxes.Forget(kind, name, id);   // stale: retired since it was resolved
            if (attempt >= MaxLiveAttempts)
                throw new InvalidOperationException($"'{name}' kept being retired and re-created under {MaxLiveAttempts} attempts");
        }
    }

    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();
        public void Dispose() { }
    }

    /// <summary>The name a storage-format error reports for a work box.</summary>
    public static string StorageName(BoxKeys.Kind kind, string name)
        => kind == BoxKeys.Kind.Service ? HighwayNames.ServiceQueue(name) : (kind == BoxKeys.Kind.DeadFeed ? $"feed:{name}" : HighwayNames.Queue(name));

    public static string GroupQueue(string channel, string group) => $"{channel}@{group}";

    /// <summary>Formats the logical feed target string: <c>SVC:&lt;service&gt;</c> or <c>Q:&lt;queue&gt;</c>.</summary>
    public static string FeedTargetName(BoxKeys.Kind kind, string name)
        => (kind == BoxKeys.Kind.Service ? "SVC:" : "Q:") + name;

    /// <summary>Parses a logical feed target string into (kind, name).</summary>
    public static bool TryParseFeedTarget(string target, out BoxKeys.Kind kind, out string name)
    {
        if (target.StartsWith("SVC:", StringComparison.OrdinalIgnoreCase))
        {
            kind = BoxKeys.Kind.Service;
            name = target[4..];
            return name.Length > 0;
        }
        if (target.StartsWith("Q:", StringComparison.OrdinalIgnoreCase))
        {
            kind = BoxKeys.Kind.Queue;
            name = target[2..];
            return name.Length > 0;
        }
        kind = default;
        name = "";
        return false;
    }

    // =====================================================================================
    // Group retirement (unsubscribe, retire-on-publish, BYE PURGE)
    // =====================================================================================

    /// <summary>
    /// Destroys a subscriber group: its queue box (one range delete, id retired) and its membership. The
    /// caller holds the group box's lock and commits; after the commit it calls
    /// <see cref="BoxRuntime.Forget"/> for each returned box. Counted before deletion so the loss is
    /// never silent (C4.3).
    /// </summary>
    public static RegistrySupport.RetirementOutcome RetireGroup(
        CommandContext ctx, IStoreBatch batch, string channel, string group, List<(BoxKeys.Kind Kind, string Name, uint Box)> retiredBoxes)
    {
        var store = ctx.Store;
        var derived = GroupQueue(channel, group);
        long messages = 0, bytes = 0;
        if (Catalog.Find(store, batch, ctx.Boxes, BoxKeys.Kind.Queue, derived) is { } box)
        {
            using (var wb = Open(ctx, box, BoxKeys.Kind.Queue, batch))
            {
                messages = wb.Ready + wb.Delayed + wb.InFlight;
                bytes = wb.Bytes;
            }
            Catalog.Retire(store, batch, ctx.Boxes, BoxKeys.Kind.Queue, derived, ctx.NowTicks);
            retiredBoxes.Add((BoxKeys.Kind.Queue, derived, box));
        }
        // 070: a retired group is gone from every member's declaration record too, so a node that still
        // declares it is seen as joining again (the session diff compares against real membership).
        using (var snap = store.Snapshot())
            foreach (var member in RegistrySupport.ReadSet(store, snap, HighwayNames.GroupMembers(channel, group)))
                RegistrySupport.SetRemove(store, batch, HighwayNames.NodeSubs(member), derived);
        store.DeleteRange(batch, HighwayKeyspace.SetPrefix(HighwayNames.GroupMembers(channel, group)));
        RegistrySupport.SetRemove(store, batch, HighwayNames.ChannelGroups(channel), group);
        return new RegistrySupport.RetirementOutcome(1, messages, bytes);
    }

    /// <summary>What happens to a group when the node leaving it was its last member (070).</summary>
    public enum LastMember
    {
        /// <summary>Stop fan-out now, keep the backlog, retire after the retirement threshold (session diff).</summary>
        Drain,
        /// <summary>Retire now, discarding the backlog, counted (BYE PURGE).</summary>
        Retire,
    }

    /// <summary>The effect of <see cref="LeaveGroup"/>, for the caller's events.</summary>
    public readonly record struct LeaveOutcome(bool OthersRemain, bool Draining, RegistrySupport.RetirementOutcome Retired);

    /// <summary>
    /// A node leaves one subscriber group (070 — the one implementation used by the session diff and BYE
    /// PURGE). Removes the membership and the node's declaration of it; when other members remain nothing
    /// else changes. When the node was the last member: <see cref="LastMember.Drain"/> marks the group queue
    /// draining (publishes skip it, its backlog is kept until the retirement threshold) — or retires it at
    /// once if it never received anything, since there is nothing to keep; <see cref="LastMember.Retire"/>
    /// retires it now. The caller holds the channel and group box locks and commits the batch.
    /// </summary>
    public static LeaveOutcome LeaveGroup(
        CommandContext ctx, IStoreBatch batch, IStoreSnapshot snap, string channel, string group, string node,
        LastMember onLast, List<(BoxKeys.Kind Kind, string Name, uint Box)> retiredBoxes)
    {
        var store = ctx.Store;
        var derived = GroupQueue(channel, group);
        var members = RegistrySupport.ReadSet(store, snap, HighwayNames.GroupMembers(channel, group));
        RegistrySupport.SetRemove(store, batch, HighwayNames.GroupMembers(channel, group), node);
        RegistrySupport.SetRemove(store, batch, HighwayNames.NodeSubs(node), derived);
        if (members.Any(m => m != node))
            return new LeaveOutcome(OthersRemain: true, Draining: false, default);

        if (onLast == LastMember.Drain && Catalog.Find(store, batch, ctx.Boxes, BoxKeys.Kind.Queue, derived) is { } box)
        {
            using var wb = Open(ctx, box, BoxKeys.Kind.Queue, batch);
            if (wb.DrainingSince == 0) wb.SetDrainingSince(ctx.NowTicks);
            return new LeaveOutcome(OthersRemain: false, Draining: true, default);
        }
        return new LeaveOutcome(false, false, RetireGroup(ctx, batch, channel, group, retiredBoxes));
    }

    /// <summary>
    /// The group queue boxes of every group in <paramref name="groups"/> that exist — for locking before a retire or a
    /// fan-out. <paramref name="live"/> reads the name index itself, not the id cache: the check a caller makes under
    /// its locks (070 third review F3), so a box created after the lock set was planned — by a claim, say — is seen
    /// and its lock taken before the group is retired. A cached id the index no longer names is forgotten, so the
    /// next plan resolves the box that is really there.
    /// </summary>
    public static List<string> GroupLockNames(CommandContext ctx, string channel, IEnumerable<string> groups, bool live = false)
    {
        var names = new List<string>();
        foreach (var group in groups)
        {
            var name = GroupQueue(channel, group);
            uint? box;
            if (live)
            {
                box = LiveBox(ctx, BoxKeys.Kind.Queue, name);
                if (ctx.Boxes.TryGetId(BoxKeys.Kind.Queue, name, out var cached) && cached != box)
                    ctx.Boxes.Forget(BoxKeys.Kind.Queue, name, cached);
            }
            else
            {
                box = Find(ctx, BoxKeys.Kind.Queue, name);
            }
            if (box is { } id)
                names.Add(WorkBox.LockName(id));
        }
        return names;
    }

    /// <summary>
    /// The locks for one attempt of a plan-lock-replan loop (session diff, BYE PURGE): the planned set, or on the last
    /// attempt every stripe (070 R2-4) — logged, since it briefly stalls every other command on the broker.
    /// </summary>
    public static IDisposable LockPlan(CommandContext ctx, bool everything, IEnumerable<string> held, string what)
    {
        if (!everything) return ctx.Locks.LockMany(held);
        ctx.Recorder.Logger?.LogWarning("{What}: the plan kept changing under its locks; completing under every lock stripe", what);
        return ctx.Locks.LockAll();
    }

    /// <summary>After the retiring batch committed: drop the retired boxes from the runtime caches.</summary>
    public static void Forget(CommandContext ctx, IEnumerable<(BoxKeys.Kind Kind, string Name, uint Box)> retired)
    {
        foreach (var (kind, name, box) in retired) ctx.Boxes.Forget(kind, name, box);
    }
}
