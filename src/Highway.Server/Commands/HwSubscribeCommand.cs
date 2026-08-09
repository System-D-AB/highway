using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.SUBSCRIBE &lt;channel&gt; &lt;group&gt; → +OK
///
/// Registers a subscriber group for a channel. If there are backlog entries
/// within the retention window, they are copied (not drained) to the new
/// group's queue. Idempotent.
/// </summary>
internal sealed class HwSubscribeCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _channel = null!;
    private string _group = null!;

    public HwSubscribeCommand(HighwayServerOptions opts, FlightRecorder recorder)
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
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            // Register the group (idempotent). The set is authoritative for the
            // new/existing decision: addedCount is 1 only for a genuinely new group.
            var groupsKey  = CreateArgSlice(HighwayKeys.ChannelGroups(_channel));
            var groupSlice = CreateArgSlice(Encoding.UTF8.GetBytes(_group));
            api.SetAdd(groupsKey, groupSlice, out var addedCount);

            // Maintain main-store group list (newline-delimited) for HW.PUBLISH.
            // Unconditional — an inconsistent mirror self-heals on the next subscribe.
            var grpListKey = CreateArgSlice(HighwayKeys.ChannelGroupList(_channel));
            PinnedSpanByte currentList;
            api.GET(grpListKey, out currentList);
            var existingGroups = currentList.Length > 0
                ? Encoding.UTF8.GetString(currentList.ReadOnlySpan).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                : [];
            if (!existingGroups.Contains(_group))
            {
                var newList = existingGroups.Length > 0
                    ? string.Join('\n', existingGroups) + "\n" + _group
                    : _group;
                api.SET(grpListKey, CreateArgSlice(newList));
            }

            // Copy backlog entries (within retention window) to the group queue —
            // ONLY for a genuinely new group. A re-subscribe (e.g. feature 005's
            // engine sends HW.SUBSCRIBE on every start) must not re-copy the
            // backlog, and a group that unsubscribed and re-subscribes IS new
            // Nothing is copied into a new group. Highway used to seed it from a
            // per-channel backlog, which meant a late subscriber received an arbitrary
            // prefix of history depending on when the first subscriber started — and the
            // copy materialised the entire backlog under an exclusive lock, which was
            // fatal at any serious size. A new group starts empty (feature 014 follow-up).

            WriteSimpleString(ref output, "OK");
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }


    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        => _recorder.Record(
            HighwayEventType.GroupRegistered, _channel ?? "?",
            nodeId: _group,
            errorCode: FailureCode);
}
