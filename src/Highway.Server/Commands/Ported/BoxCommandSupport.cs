using System.Globalization;
using System.Text;
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
        => new(ctx.Store, ctx.Boxes, box, tracksBytes: kind == BoxKeys.Kind.Queue, sharedBatch);

    public static IDisposable Lock(CommandContext ctx, uint box) => ctx.Locks.Lock(WorkBox.LockName(box));

    /// <summary>The name a storage-format error reports for a work box.</summary>
    public static string StorageName(BoxKeys.Kind kind, string name)
        => kind == BoxKeys.Kind.Service ? HighwayNames.ServiceQueue(name) : HighwayNames.Queue(name);

    public static string GroupQueue(string channel, string group) => $"{channel}@{group}";

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
        store.DeleteRange(batch, HighwayKeyspace.SetPrefix(HighwayNames.GroupMembers(channel, group)));
        RegistrySupport.SetRemove(store, batch, HighwayNames.ChannelGroups(channel), group);
        RegistrySupport.SetRemove(store, batch, HighwayNames.NodeChannels(group), channel);
        return new RegistrySupport.RetirementOutcome(1, messages, bytes);
    }

    /// <summary>The group queue boxes of every group in <paramref name="groups"/> that exist — for locking before a retire or a fan-out.</summary>
    public static List<string> GroupLockNames(CommandContext ctx, string channel, IEnumerable<string> groups)
    {
        var names = new List<string>();
        foreach (var group in groups)
            if (Find(ctx, BoxKeys.Kind.Queue, GroupQueue(channel, group)) is { } box)
                names.Add(WorkBox.LockName(box));
        return names;
    }

    /// <summary>After the retiring batch committed: drop the retired boxes from the runtime caches.</summary>
    public static void Forget(CommandContext ctx, IEnumerable<(BoxKeys.Kind Kind, string Name, uint Box)> retired)
    {
        foreach (var (kind, name, box) in retired) ctx.Boxes.Forget(kind, name, box);
    }
}
