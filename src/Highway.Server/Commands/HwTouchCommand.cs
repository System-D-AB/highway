using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Abstractions.Observability;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.TOUCH &lt;target...&gt; &lt;id&gt; → :1 renewed | :0 not found (feature 019).
///
/// <code>
/// HW.TOUCH SVC &lt;service&gt; &lt;node&gt; &lt;requestId&gt;
/// HW.TOUCH Q   &lt;queue&gt;   &lt;node&gt; &lt;messageId&gt;
/// </code>
///
/// <para><b>What it is for.</b> A worker that claims a message starts a clock. Before this
/// command there was no way to say "still working", so a handler that outlived
/// <c>Lease</c> had its message requeued <b>while it was still running</b> — not a duplicate
/// after a failure, but a concurrent duplicate caused by nothing more than being slow. A
/// twenty-minute job against a five-minute lease ran five times and then dead-lettered.</para>
///
/// <para><b>No new field, no new framing, no new key.</b> The sweep decides expiry by comparing
/// the claim timestamp, so moving that timestamp forward <i>is</i> restarting the lease. This
/// is 015's entry-rewrite pattern applied to one field.</para>
///
/// <para><b>It changes nothing else.</b> Not the attempt count, not the byte counters, not the
/// entry's position. Only the deadline moves. In particular the <b>failure block survives</b>:
/// an entry that reported a failure and is then renewed must not lose its <c>firstType</c>.</para>
///
/// <para>Renewing a message that is no longer in the processing list returns <c>:0</c>. A late
/// renewal is a race the client cannot avoid, not an error to investigate.</para>
/// </summary>
internal sealed class HwTouchCommand : HighwayCommandBase
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

    public HwTouchCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState() => _idBytes = [];

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;

        var kindArg = GetNextArg(ref procInput, ref idx);
        if (kindArg.Length == 0)
        {
            Fail(HighwayErrors.InvalidArg, "HW.TOUCH requires a target: SVC or Q");
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
                // A subscriber group's queue is a queue (018), so "{channel}@{group}" arrives
                // here as an ordinary queue name and needs no third form.
                if (!TryReadIdentifier(ref procInput, ref idx, "queue", _opts.MaxIdentifierBytes, out _name)) return true;
                if (!TryReadIdentifier(ref procInput, ref idx, "node", _opts.MaxIdentifierBytes, out _scope)) return true;
                _procKey = HighwayKeys.QueueProcessing(_name, _scope);
                break;

            default:
                Fail(HighwayErrors.InvalidArg,
                    $"unknown target '{kind}'; expected SVC <service> <node> or Q <queue> <node>");
                return true;
        }

        if (!TryReadIdentifier(ref procInput, ref idx, "id", _opts.MaxIdentifierBytes, out _id, out _idBytes))
            return true;

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

            var renewed = false;
            var now = DateTime.UtcNow.Ticks;

            foreach (var entry in entries)
            {
                var span = entry.ReadOnlySpan;
                var rewritten = span.ToArray();

                if (!renewed && !Envelope.IsLegacyEntry(span))
                {
                    Envelope.DecodeRpcProcessingEntry(span, out _, out var id, out var payload, out var attempts);

                    if (id.SequenceEqual(_idBytes))
                    {
                        renewed = true;

                        // Only the claim timestamp moves. The attempt count rides along
                        // unchanged, and the failure block is carried across explicitly —
                        // rebuilding an entry from its decoded parts drops the trailer, which
                        // is how 015 lost it at three of four re-encode sites.
                        rewritten = Envelope.CarryFailureBlock(
                            span,
                            Envelope.EncodeRpcProcessingEntry(now, id, payload, attempts));
                    }
                }

                api.ListRightPush(procKey, CreateArgSlice(rewritten), out _);
            }

            WriteInteger(ref output, renewed ? 1 : 0);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        // Individual renewals are deliberately NOT recorded (R3.5). At a one-minute interval
        // across many in-flight messages they would flood the recorder with the least
        // interesting thing it could hold. Only a rejected call is worth an event.
        if (FailureCode is not null)
        {
            _recorder.Record(
                HighwayEventType.LeaseRenewed, _name ?? "?",
                nodeId: _scope,
                requestId: _id,
                errorCode: FailureCode);
        }
    }
}
