using Highway.Server.Commands.Runtime;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.UNSUBSCRIBE &lt;channel&gt; &lt;group&gt; → +OK
///
/// Removes a subscriber group, deleting its pending queue and everything it owns. Idempotent.
///
/// <para>Ported from Garnet. The teardown is the shared <see cref="RegistrySupport.RetireGroup"/>
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
        var store = ctx.Store;

        using var _lock = ctx.Locks.Lock(_channel);
        using var snap = store.Snapshot();
        using var batch = store.NewBatch();

        RegistrySupport.RetireGroup(store, snap, batch, _channel, _group);

        batch.Commit();
        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
        => ctx.Recorder.Record(
            HighwayEventType.GroupRemoved, _channel,
            nodeId: _group, errorCode: FailureCode);
}
