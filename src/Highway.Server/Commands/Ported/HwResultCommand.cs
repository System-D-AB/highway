using Highway.Server.Commands.Runtime;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.RESULT &lt;requestId&gt; → the reply payload, removing it — or a nil bulk string when no reply is waiting
/// (5.0; replaces stock GET + DEL on <c>hw:rep:{requestId}</c>).
///
/// <para>An atomic take: a caller that reads the reply also consumes it, in one command, so two readers can
/// never both collect it and a crashed reader never leaves a slot that outlives its TTL. As an
/// <c>HW.*</c> command it rides the client's retry and <c>NOTPRIMARY</c> handling like every other
/// command — the stock reads did not.</para>
/// </summary>
internal sealed class HwResultCommand : HighwayCommand
{
    private string _requestId = null!;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        return TryReadIdentifier(input, ref idx, "requestId", ctx.Options.MaxIdentifierBytes, out _requestId);
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var slotKey = HighwayKeyspace.Kv(HighwayNames.ReplySlot(_requestId));
        using var _lock = ctx.Locks.Lock(_requestId);
        using var snap = ctx.Store.Snapshot();
        var reply = ctx.Store.GetLive(snap, slotKey, ctx.NowTicks);
        if (reply is null)
        {
            writer.BulkString(null);
            return;
        }
        using var batch = ctx.Store.NewBatch();
        ctx.Store.Delete(batch, slotKey);   // its expiry-index entry is dropped by the next sweep
        batch.Commit();
        writer.BulkString(reply);
    }
}
