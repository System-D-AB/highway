using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.FAIL (SVC &lt;service&gt; | Q &lt;queue&gt;) &lt;node&gt; &lt;id&gt; &lt;type&gt; &lt;detail&gt;
/// → :1 recorded | :0 not found (feature 015).
///
/// Attaches a failure block to a claimed entry <b>without acknowledging it</b>: drain the
/// processing list, rewrite the matched entry in place with the failure block (preserving
/// <c>firstType</c> across attempts), restore the rest. The message stays claimed and the
/// lease sweep recovers it on the same schedule. Reporting is orthogonal to delivery.
/// </summary>
internal sealed class HwFailCommand : HighwayCommand
{
    private const string TargetService = "SVC";
    private const string TargetQueue = "Q";

    private string _procListName = null!;
    private string _name = null!;
    private string _scope = null!;
    private string _id = null!;
    private byte[] _idBytes = [];
    private byte[] _typeBytes = [];
    private byte[] _detailBytes = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var kindArg = input.Next(ref idx);
        if (kindArg.Length == 0)
            return Fail(HighwayErrors.InvalidArg, "HW.FAIL requires a target: SVC, Q or CH");

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
                    $"unknown target '{kind}'; accepted forms are SVC <service> <node> or Q <queue> <node>");
        }

        if (!TryReadIdentifier(input, ref idx, "id", ctx.Options.MaxIdentifierBytes, out _id, out _idBytes)) return false;

        _typeBytes = input.Next(ref idx).ToArray();
        if (_typeBytes.Length > ushort.MaxValue)
            return Fail(HighwayErrors.InvalidArg, $"exception type is {_typeBytes.Length} bytes; maximum is {ushort.MaxValue}");

        _detailBytes = input.Next(ref idx).ToArray();
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        using var _lock = ctx.Locks.Lock(_scope);
        using var batch = ctx.Store.NewBatch();

        var found = false;
        ConsumerSupport.DrainAndRestore(ctx.Store, batch, _procListName, entry =>
        {
            if (!found && !Envelope.IsLegacyEntry(entry) && Matches(entry))
            {
                found = true;
                return Envelope.WithFailureBlock(entry, BuildBlock(ctx, entry));
            }
            return entry;
        });

        batch.Commit();
        writer.Integer(found ? 1 : 0);
    }

    protected override void AfterCommit(CommandContext ctx)
        => ctx.Recorder.Record(
            HighwayEventType.DeliveryFailed, _name ?? "?",
            nodeId: _scope, requestId: _id,
            errorCode: FailureCode ?? (_typeBytes.Length > 0 ? Encoding.UTF8.GetString(_typeBytes) : null));

    private bool Matches(ReadOnlySpan<byte> entry)
    {
        Envelope.DecodeRpcProcessingEntry(entry, out _, out var id, out _, out _);
        return id.SequenceEqual(_idBytes);
    }

    /// <summary>Builds the failure block, preserving firstType across attempts (see the Garnet original).</summary>
    private byte[] BuildBlock(CommandContext ctx, ReadOnlySpan<byte> entry)
    {
        ReadOnlySpan<byte> firstType = default;

        if (Envelope.TryGetFailureBlock(entry, out var existing, out _))
        {
            Envelope.DecodeFailureBlock(existing, out var prevType, out var prevFirst, out _);
            firstType = prevFirst.Length > 0
                ? prevFirst
                : prevType.SequenceEqual(_typeBytes)
                    ? default
                    : prevType;
        }

        // Feature 002 capture mode governs the detail (an exception message routinely carries
        // application data); the TYPE survives either way as metadata.
        var detail = ctx.Recorder.CaptureFor(_name) == PayloadCapture.Full ? _detailBytes : [];
        return Envelope.EncodeFailureBlock(_typeBytes, firstType, detail);
    }
}
