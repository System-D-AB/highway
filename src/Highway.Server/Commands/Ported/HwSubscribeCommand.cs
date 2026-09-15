using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.SUBSCRIBE &lt;channel&gt; &lt;group&gt; [&lt;node&gt;] → +OK
///
/// Registers a subscriber group for a channel and the node's membership in it (feature 025).
/// A group may be backed by several nodes — replicas of one logical subscriber — competing for
/// its queue; membership lets retirement count the youngest member. Idempotent.
///
/// <para>Ported from Garnet. The mirror collapse (T3.3) is total here: <c>grplist</c>,
/// <c>node:channels</c>, <c>grp:members</c> and <c>node:subs</c> were four newline-delimited
/// mirror strings; all four are now plain sets written with <see cref="RegistrySupport.SetAdd"/>.
/// The <c>added</c> answer that the Garnet mirror could only approximate is now the set's own
/// authority (<see cref="Highway.Server.Storage.IHighwayStore.SetAdd"/> returns whether newly
/// added). No backlog is copied into a new group (014 follow-up) — a new group starts empty.</para>
/// </summary>
internal sealed class HwSubscribeCommand : HighwayCommand
{
    private string _channel = null!;
    private string _group = null!;
    private string _node = null!;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "channel", ctx.Options.MaxIdentifierBytes, out _channel))
            return false;
        if (!TryReadIdentifier(input, ref idx, "group", ctx.Options.MaxIdentifierBytes, out _group))
            return false;

        // 025: the node argument is OPTIONAL (arity -3). Absent means the pre-025 identity —
        // the group IS the node — so an old client's two-argument subscribe keeps meaning
        // exactly what it always meant, and membership degrades to {group}.
        var nodeArg = input.Next(ref idx);
        if (nodeArg.Length == 0)
        {
            _node = _group;
        }
        else if (Identifier.IsValid(nodeArg, ctx.Options.MaxIdentifierBytes))
        {
            _node = Encoding.UTF8.GetString(nodeArg);
        }
        else
        {
            return Fail(HighwayErrors.InvalidArg, IdentifierErrorDetail(nodeArg, "node", ctx.Options.MaxIdentifierBytes));
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        using var _lock = ctx.Locks.Lock(_channel);
        using var batch = store.NewBatch();

        // Register the group (idempotent). The set is authoritative for new/existing.
        store.SetAdd(batch, HighwayKeyspace.SetPrefix(HighwayNames.ChannelGroups(_channel)), Encoding.UTF8.GetBytes(_group));

        // Every former mirror is a plain set now (T3.3). Idempotent.
        RegistrySupport.SetAdd(store, batch, HighwayNames.NodeChannels(_group), _channel);
        RegistrySupport.SetAdd(store, batch, HighwayNames.GroupMembers(_channel, _group), _node);
        RegistrySupport.SetAdd(store, batch, HighwayNames.NodeSubs(_node), $"{_channel}@{_group}");

        batch.Commit();
        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
        => ctx.Recorder.Record(
            HighwayEventType.GroupRegistered, _channel,
            nodeId: _group, errorCode: FailureCode);
}
