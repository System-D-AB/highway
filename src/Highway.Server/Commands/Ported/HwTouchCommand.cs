using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.TOUCH (SVC &lt;service&gt; | Q &lt;queue&gt;) &lt;node&gt; &lt;id&gt; → :1 renewed | :0 not found
/// (feature 019). Renews a claimed entry's lease without acknowledging it: drain the
/// processing list, rewrite the matched entry's claim timestamp to now (attempt count and
/// failure block carried across), restore the rest in order.
/// </summary>
internal sealed class HwTouchCommand : HighwayCommand
{
    private const string TargetService = "SVC";
    private const string TargetQueue = "Q";

    private string _procListName = null!;
    private string _name = null!;
    private string _scope = null!;
    private byte[] _idBytes = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var kindArg = input.Next(ref idx);
        if (kindArg.Length == 0)
            return Fail(HighwayErrors.InvalidArg, "HW.TOUCH requires a target: SVC or Q");

        var kind = Encoding.ASCII.GetString(kindArg).ToUpperInvariant();
        switch (kind)
        {
            case TargetService:
                if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _name)) return false;
                if (!TryReadIdentifier(input, ref idx, "node", ctx.Options.MaxIdentifierBytes, out _scope)) return false;
                _procListName = HighwayNames.ServiceProcessing(_name, _scope);
                break;
            case TargetQueue:
                if (!TryReadDerivedIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out _name)) return false;
                if (!TryReadIdentifier(input, ref idx, "node", ctx.Options.MaxIdentifierBytes, out _scope)) return false;
                _procListName = HighwayNames.QueueProcessing(_name, _scope);
                break;
            default:
                return Fail(HighwayErrors.InvalidArg,
                    $"unknown target '{kind}'; expected SVC <service> <node> or Q <queue> <node>");
        }

        if (!TryReadIdentifier(input, ref idx, "id", ctx.Options.MaxIdentifierBytes, out _, out _idBytes))
            return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        using var _lock = ctx.Locks.Lock(_scope);
        using var batch = ctx.Store.NewBatch();

        var renewed = false;
        ConsumerSupport.DrainAndRestore(ctx.Store, batch, _procListName, entry =>
        {
            if (!renewed && !Envelope.IsLegacyEntry(entry))
            {
                Envelope.DecodeRpcProcessingEntry(entry, out _, out var id, out var payload, out var attempts);
                if (id.SequenceEqual(_idBytes))
                {
                    renewed = true;
                    // Only the claim timestamp moves. The failure block is carried across
                    // explicitly (rebuilding from parts would drop the trailer — 015's trap).
                    return Envelope.CarryFailureBlock(
                        entry,
                        Envelope.EncodeRpcProcessingEntry(ctx.NowTicks, id.ToArray(), payload.ToArray(), attempts));
                }
            }
            return entry;
        });

        batch.Commit();
        writer.Integer(renewed ? 1 : 0);
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        // Individual renewals are deliberately not recorded (R3.5) — only a rejected call.
        if (FailureCode is not null)
            ctx.Recorder.Record(HighwayEventType.LeaseRenewed, _name ?? "?", nodeId: _scope, errorCode: FailureCode);
    }
}
