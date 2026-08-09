using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.UNSUBSCRIBE &lt;channel&gt; &lt;group&gt; → +OK
///
/// Removes a subscriber group, deleting its pending queue and processing list.
/// Idempotent.
/// </summary>
internal sealed class HwUnsubscribeCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _channel = null!;
    private string _group = null!;

    public HwUnsubscribeCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "channel", _opts.MaxIdentifierBytes, out _channel))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "group", _opts.MaxIdentifierBytes, out _group))
            return true;

        AddKey(CreateArgSlice(HighwayKeys.ChannelGroups(_channel)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.ChannelGroupList(_channel)), LockType.Exclusive, StoreType.Main);
        // Lock every key the group's queue owns, not just the list (017 T2). Before this,
        // unsubscribing deleted the queue and left its byte counter, delayed set, dead-letter
        // list and processing list behind — a leak 016's byte accounting made visible, because
        // the stale counter kept charging a queue that no longer existed.
        AddKey(CreateArgSlice(HighwayKeys.NodeChannels(_group)), LockType.Exclusive, StoreType.Main);

        var (objectKeys, mainKeys) = GroupQueueKeys(_channel, _group);
        foreach (var key in objectKeys)
            AddKey(CreateArgSlice(key), LockType.Exclusive, StoreType.Object);
        foreach (var key in mainKeys)
            AddKey(CreateArgSlice(key), LockType.Exclusive, StoreType.Main);

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            // One shared retirement (017 T2): deletes the queue and everything it owns,
            // unregisters the group, and maintains both mirror lists.
            RetireGroup(api, _channel, _group);

            WriteSimpleString(ref output, "OK");
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        => _recorder.Record(
            HighwayEventType.GroupRemoved, _channel ?? "?",
            nodeId: _group,
            errorCode: FailureCode);
}
