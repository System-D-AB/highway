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
        // Lock the derived queue key so it can be cleaned up.
        var derivedName = $"{_channel}@{_group}";
        AddKey(CreateArgSlice(HighwayKeys.Queue(derivedName)), LockType.Exclusive, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var groupsKey    = CreateArgSlice(HighwayKeys.ChannelGroups(_channel));
            var groupSlice    = CreateArgSlice(Encoding.UTF8.GetBytes(_group));

            api.SetRemove(groupsKey, groupSlice, out _);

            // Delete the derived queue key.
            var derivedName = $"{_channel}@{_group}";
            var derivedQueueKey = CreateArgSlice(HighwayKeys.Queue(derivedName));
            api.DELETE(derivedQueueKey);

            // Maintain main-store group list
            var grpListKey = CreateArgSlice(HighwayKeys.ChannelGroupList(_channel));
            PinnedSpanByte currentList;
            api.GET(grpListKey, out currentList);
            if (currentList.Length > 0)
            {
                var groups = Encoding.UTF8.GetString(currentList.ReadOnlySpan)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(g => g != _group)
                    .ToArray();
                if (groups.Length > 0)
                    api.SET(grpListKey, CreateArgSlice(string.Join('\n', groups)));
                else
                    api.DELETE(grpListKey);
            }

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
