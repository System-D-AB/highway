using Garnet.common;
using System.Text;
using Garnet.server;
using Highway.Abstractions.Observability;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.FAIL &lt;target...&gt; &lt;id&gt; &lt;type&gt; &lt;detail&gt; → :1 recorded | :0 not found (feature 015).
///
/// <code>
/// HW.FAIL SVC &lt;service&gt; &lt;node&gt;  &lt;requestId&gt; &lt;type&gt; &lt;detail&gt;
/// HW.FAIL Q   &lt;queue&gt;   &lt;node&gt;  &lt;messageId&gt; &lt;type&gt; &lt;detail&gt;
/// </code>
///
/// <para><b>One command, not three.</b> The target grammar is the one <c>HW.DLQ</c> already
/// parses; three per-family commands would triplicate parsing and validation for no gain.</para>
///
/// <para><b>It does not acknowledge.</b> The message stays in the processing list and the lease
/// sweep recovers it exactly as before. Reporting is orthogonal to delivery: a client that
/// reports a failure has not finished with the message, it has explained itself.</para>
///
/// <para><b>Why the exception type is a separate argument</b> rather than a field inside
/// <c>detail</c>: merging has to preserve <c>firstType</c> across attempts, and reading it out
/// of a JSON blob would mean parsing JSON inside a Garnet transaction on the failure path. The
/// type is the one field the server itself needs, so it travels where the server can see it.</para>
///
/// <para>Reporting against a message that is no longer in the processing list — already
/// acknowledged, or swept — returns <c>:0</c> and does nothing. A late report is a race the
/// client cannot avoid, not an error to investigate.</para>
/// </summary>
internal sealed class HwFailCommand : HighwayCommandBase
{
    private const string TargetService = "SVC";
    private const string TargetQueue = "Q";

    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _procKey = null!;
    private string _name = null!;
    private string _scope = null!;
    private string _id = null!;
    private byte[] _idBytes = [];
    private byte[] _typeBytes = [];
    private byte[] _detailBytes = [];

    public HwFailCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState()
    {
        _idBytes = [];
        _typeBytes = [];
        _detailBytes = [];
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;

        var kindArg = GetNextArg(ref procInput, ref idx);
        if (kindArg.Length == 0)
        {
            Fail(HighwayErrors.InvalidArg, "HW.FAIL requires a target: SVC, Q or CH");
            return true;
        }

        var kind = Encoding.ASCII.GetString(kindArg.ReadOnlySpan).ToUpperInvariant();
        switch (kind)
        {
            case TargetService:
                if (!TryReadIdentifier(ref procInput, ref idx, "service", _opts.MaxIdentifierBytes, out _name)) return true;
                if (!TryReadIdentifier(ref procInput, ref idx, "node", _opts.MaxIdentifierBytes, out _scope)) return true;
                _procKey = HighwayKeys.ServiceProcessing(_name, _scope);
                break;

            case TargetQueue:
                if (!TryReadDerivedIdentifier(ref procInput, ref idx, "queue", _opts.MaxIdentifierBytes, out _name)) return true;
                if (!TryReadIdentifier(ref procInput, ref idx, "node", _opts.MaxIdentifierBytes, out _scope)) return true;
                _procKey = HighwayKeys.QueueProcessing(_name, _scope);
                break;

            default:
                Fail(HighwayErrors.InvalidArg,
                    $"unknown target '{kind}'; accepted forms are SVC <service> <node> or Q <queue> <node>");
                return true;
        }

        if (!TryReadIdentifier(ref procInput, ref idx, "id", _opts.MaxIdentifierBytes, out _id, out _idBytes))
            return true;

        var typeArg = GetNextArg(ref procInput, ref idx);
        _typeBytes = typeArg.ReadOnlySpan.ToArray();
        if (_typeBytes.Length > ushort.MaxValue)
        {
            Fail(HighwayErrors.InvalidArg, $"exception type is {_typeBytes.Length} bytes; maximum is {ushort.MaxValue}");
            return true;
        }

        var detailArg = GetNextArg(ref procInput, ref idx);
        _detailBytes = detailArg.ReadOnlySpan.ToArray();

        // The processing list is derivable from the command's own arguments, so it can be
        // declared here. That is exactly why the failure block lives in the entry and not in
        // a per-message side key, which could not be declared and would be rejected in Main.
        AddKey(CreateArgSlice(_procKey), LockType.Exclusive, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var procKey = CreateArgSlice(_procKey);
            var status = api.ListLeftPop(procKey, int.MaxValue, out var entries);

            if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
            {
                WriteInteger(ref output, 0);
                return;
            }

            var found = false;
            foreach (var entry in entries)
            {
                var span = entry.ReadOnlySpan;
                var rewritten = span.ToArray();

                if (!found && !Envelope.IsLegacyEntry(span) && Matches(span))
                {
                    found = true;
                    rewritten = Envelope.WithFailureBlock(span, BuildBlock(span));
                }

                api.ListRightPush(procKey, CreateArgSlice(rewritten), out _);
            }

            // Deliberately no acknowledgement: the entry is rewritten in place, so the lease
            // sweep still finds it and still recovers it on exactly the schedule it would have.
            WriteInteger(ref output, found ? 1 : 0);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    private bool Matches(ReadOnlySpan<byte> entry)
    {
        Envelope.DecodeRpcProcessingEntry(entry, out _, out var id, out _, out _);
        return id.SequenceEqual(_idBytes);
    }

    /// <summary>
    /// Builds the replacement block, preserving <c>firstType</c> across attempts.
    ///
    /// <para>Set once, never overwritten, and only when the failure actually changed shape.
    /// An operator asks "did this fail the same way every time?" — a <c>firstType</c> equal to
    /// <c>type</c> would be noise, and one that moved with every attempt would answer a
    /// different question than the one asked.</para>
    /// </summary>
    private byte[] BuildBlock(ReadOnlySpan<byte> entry)
    {
        ReadOnlySpan<byte> firstType = default;

        if (Envelope.TryGetFailureBlock(entry, out var existing, out _))
        {
            Envelope.DecodeFailureBlock(existing, out var prevType, out var prevFirst, out _);

            firstType = prevFirst.Length > 0
                ? prevFirst                                       // already established
                : prevType.SequenceEqual(_typeBytes)
                    ? default                                     // still failing the same way
                    : prevType;                                   // it just changed - record where it started
        }

        // Feature 002's capture mode governs the detail, because an exception message
        // routinely contains application data and a name whose payloads are withheld must not
        // have that data arrive through the failure path instead (R3.5). The TYPE survives
        // either way: it is metadata, and it is the field that makes a dead letter
        // diagnosable at all.
        var detail = _recorder.CaptureFor(_name) == PayloadCapture.Full
            ? _detailBytes
            : [];

        return Envelope.EncodeFailureBlock(_typeBytes, firstType, detail);
    }

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        => _recorder.Record(
            HighwayEventType.DeliveryFailed, _name ?? "?",
            nodeId: _scope,
            requestId: _id,
            errorCode: FailureCode ?? (_typeBytes.Length > 0 ? Encoding.UTF8.GetString(_typeBytes) : null));
}
