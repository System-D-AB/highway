using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Boxes;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// The 5.0 settle commands (068 C2): each names a claim by the token HW.CLAIM returned. The token carries
/// the box and the claim's sequence, so settling is one point lookup under the box's lock — no list of
/// in-flight work is read. A token that parses but is not held (acked, recovered by the lease sweep and
/// redelivered under a new token, or never issued) answers <c>:0</c>: a late settle is a race the client
/// cannot avoid, not an error. Only a token that does not parse is <c>HW_INVALID_TOKEN</c>.
/// </summary>
internal abstract class TokenCommand : HighwayCommand
{
    protected uint Box { get; private set; }
    protected ulong Seq { get; private set; }
    protected BoxKeys.Kind Kind { get; private set; }
    protected string? Name { get; private set; }
    protected string? Node { get; private set; }
    protected string? Id { get; private set; }

    protected bool ReadToken(CommandInput input, ref int idx)
    {
        if (!BoxCommandSupport.TryParseToken(input.Next(ref idx), out var box, out var seq))
            return Fail(HighwayErrors.InvalidToken, "a token is the value HW.CLAIM returned, echoed verbatim");
        Box = box;
        Seq = seq;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (BoxCommandSupport.Describe(ctx, Box) is not { } meta)
        {
            writer.Integer(0);   // the box was retired (or never existed): nothing is held
            return;
        }
        (Kind, Name) = meta;

        using var _lock = BoxCommandSupport.Lock(ctx, Box);
        // Under the lock (070 review R2-1): a group box retired since its metadata was cached holds nothing,
        // and writing into it would land in a deleted range.
        if (BoxCommandSupport.CanRetire(Kind, Name!) && BoxCommandSupport.LiveBox(ctx, Kind, Name!) != Box)
        {
            ctx.Boxes.Forget(Kind, Name!, Box);
            writer.Integer(0);
            return;
        }
        using var wb = BoxCommandSupport.Open(ctx, Box, Kind);
        if (wb.ReadInFlight(Seq) is { } held)
        {
            Node = held.Node;
            ReadOnlySpan<byte> id;
            if (Kind == BoxKeys.Kind.DeadFeed) Envelope.DecodeRpcEntryRaw(held.Entry, out id, out _, out _);   // no trailers of its own
            else Envelope.DecodeRpcEntry(held.Entry, out id, out _, out _);
            Id = Encoding.UTF8.GetString(id);
        }
        var done = Settle(ctx, wb);
        wb.Commit();
        writer.Integer(done ? 1 : 0);
    }

    protected abstract bool Settle(CommandContext ctx, WorkBox wb);
}

/// <summary>HW.ACK &lt;token&gt; → :1 removed | :0 not held. After an ack the message can never be redelivered.</summary>
internal sealed class HwAckCommand : TokenCommand
{
    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        return ReadToken(input, ref idx);
    }

    protected override bool Settle(CommandContext ctx, WorkBox wb) => wb.Ack(Seq);

    protected override void AfterCommit(CommandContext ctx)
    {
        ctx.Recorder.Record(
            Kind == BoxKeys.Kind.Service ? HighwayEventType.RpcAcknowledged : HighwayEventType.QueueAcknowledged,
            Name ?? "?", nodeId: Node, requestId: Id, errorCode: FailureCode);
        if (!Failed && Id is not null && Kind == BoxKeys.Kind.Queue) ctx.Metrics?.RecordAcknowledged();
    }
}

/// <summary>
/// HW.TOUCH &lt;token&gt; → :1 renewed | :0 not held. "Still working": the claim's lease restarts now; nothing else
/// changes. Renewal is bounded by the client (<c>MaxProcessingTime</c>), never by the server.
/// </summary>
internal sealed class HwTouchCommand : TokenCommand
{
    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        return ReadToken(input, ref idx);
    }

    protected override bool Settle(CommandContext ctx, WorkBox wb)
        => wb.Touch(Seq, ctx.NowTicks, Kind == BoxKeys.Kind.Service ? RpcDeadlines.Policy(ctx) : null);

    protected override void AfterCommit(CommandContext ctx)
    {
        // Individual renewals are deliberately not recorded (019 R3.5) — only a rejected call.
        if (FailureCode is not null)
            ctx.Recorder.Record(HighwayEventType.LeaseRenewed, Name ?? "?", nodeId: Node, errorCode: FailureCode);
    }
}

/// <summary>
/// HW.FAIL &lt;token&gt; &lt;type&gt; &lt;detail&gt; → :1 recorded | :0 not held. Records why a delivery failed on the
/// claimed entry's failure block, without acknowledging it — the lease sweep recovers the message on
/// exactly the schedule it would have (015). <c>firstType</c> is kept once the failure changes shape.
/// </summary>
internal sealed class HwFailCommand : TokenCommand
{
    private byte[] _typeBytes = [];
    private byte[] _detailBytes = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!ReadToken(input, ref idx)) return false;
        _typeBytes = input.Next(ref idx).ToArray();
        if (_typeBytes.Length > ushort.MaxValue)
            return Fail(HighwayErrors.InvalidArg, $"exception type is {_typeBytes.Length} bytes; maximum is {ushort.MaxValue}");
        _detailBytes = input.Next(ref idx).ToArray();
        return true;
    }

    protected override bool Settle(CommandContext ctx, WorkBox wb) => wb.Fail(Seq, entry => BuildBlock(ctx, entry));

    private byte[] BuildBlock(CommandContext ctx, byte[] entry)
    {
        ReadOnlySpan<byte> firstType = default;
        if (Envelope.TryGetFailureBlock(entry, out var existing, out _))
        {
            Envelope.DecodeFailureBlock(existing, out var prevType, out var prevFirst, out _);
            firstType = prevFirst.Length > 0
                ? prevFirst
                : prevType.SequenceEqual(_typeBytes) ? default : prevType;
        }
        var detail = ctx.Recorder.CaptureFor(Name!) == PayloadCapture.Full ? _detailBytes : [];
        return Envelope.EncodeFailureBlock(_typeBytes, firstType, detail);
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        ctx.Recorder.Record(
            HighwayEventType.DeliveryFailed, Name ?? "?",
            nodeId: Node, requestId: Id,
            errorCode: FailureCode ?? (_typeBytes.Length > 0 ? Encoding.UTF8.GetString(_typeBytes) : null));
        if (!Failed) ctx.Metrics?.RecordFailed();
    }
}
