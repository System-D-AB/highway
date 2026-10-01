using Highway.Server.Commands.Runtime;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.UNSUBSCRIBE &lt;channel&gt; &lt;group&gt; → +OK
///
/// Removes a subscriber group, deleting its pending queue and everything it owns. Idempotent.
///
/// <para>Ported from Garnet. The teardown is the shared <see cref="BoxCommandSupport.RetireGroup"/>
/// — the one implementation that <c>HW.UNSUBSCRIBE</c>, <c>HW.HEARTBEAT BYE PURGE</c>, and the
/// automatic retirement all use (017). Where Garnet enumerated and DELETEd each of the group's
/// keys, retirement now issues one <see cref="Highway.Server.Storage.IHighwayStore.DeleteRange"/>
/// per structure (037 §2). Byte counter, delayed set, DLQ, processing list and membership are
/// all reclaimed at the next compaction (C4.6).</para>
/// </summary>
internal sealed class HwUnsubscribeCommand : HighwayCommand
{
    private string _channel = null!;
    private string _group = null!;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "channel", ctx.Options.MaxIdentifierBytes, out _channel))
            return false;
        if (!TryReadIdentifier(input, ref idx, "group", ctx.Options.MaxIdentifierBytes, out _group))
            return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        // 065: the group's queue box goes in one range delete, under its lock and the channel's.
        // Re-check under the locks (070 review R2-3): a box created for the group after the lock set was
        // computed must not be retired without its lock — a concurrent claim could write into its range.
        for (var attempt = 1; ; attempt++)
        {
            var lockNames = BoxCommandSupport.GroupLockNames(ctx, _channel, [_group]);
            lockNames.Add(_channel);
            using var _lock = ctx.Locks.LockMany(lockNames);
            var live = BoxCommandSupport.LiveBox(ctx, Storage.Boxes.BoxKeys.Kind.Queue, BoxCommandSupport.GroupQueue(_channel, _group));
            if (live is { } box && !lockNames.Contains(Storage.Boxes.WorkBox.LockName(box)))
            {
                if (attempt >= 16)
                    throw new InvalidOperationException($"HW.UNSUBSCRIBE {_channel} {_group}: the group's box kept changing");
                continue;
            }
            using var batch = ctx.Store.NewBatch();
            var retired = new List<(Storage.Boxes.BoxKeys.Kind, string, uint)>();
            BoxCommandSupport.RetireGroup(ctx, batch, _channel, _group, retired);
            batch.Commit();
            BoxCommandSupport.Forget(ctx, retired);
            break;
        }
        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
        => ctx.Recorder.Record(
            HighwayEventType.GroupRemoved, _channel,
            nodeId: _group, errorCode: FailureCode);
}
