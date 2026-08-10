using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.SUBSCRIBE &lt;channel&gt; &lt;group&gt; &lt;node&gt; → +OK
///
/// Registers a subscriber group for a channel, and the node's membership in it
/// (feature 025). A group may be backed by several nodes — replicas of one
/// logical subscriber — which compete for its queue; membership is what lets
/// retirement count the youngest member instead of assuming group == node.
/// Idempotent.
/// </summary>
internal sealed class HwSubscribeCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _channel = null!;
    private string _group = null!;
    private string _node = null!;

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
        // 025: the node argument is OPTIONAL (arity -3). Absent means the pre-025 identity --
        // the group IS the node -- so an old client's two-argument subscribe keeps meaning
        // exactly what it always meant, and membership degrades to {group}.
        var nodeArg = GetNextArg(ref procInput, ref idx);
        if (nodeArg.Length == 0)
        {
            _node = _group;
        }
        else if (Identifier.IsValid(nodeArg.ReadOnlySpan, _opts.MaxIdentifierBytes))
        {
            _node = Encoding.UTF8.GetString(nodeArg.ReadOnlySpan);
        }
        else
        {
            // Fail returns false; returning it directly would abort as a bare transient
            // error and invite retries of a permanently invalid identifier (028 found this
            // same slip in HW.JOB — the convention is Fail(...) then return true).
            Fail(HighwayErrors.InvalidArg,
                IdentifierErrorDetail(nodeArg.ReadOnlySpan, "node", _opts.MaxIdentifierBytes));
            return true;
        }

        AddKey(CreateArgSlice(HighwayKeys.ChannelGroups(_channel)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.ChannelGroupList(_channel)), LockType.Exclusive, StoreType.Main);

        // 017: the reverse index — which channels this group subscribes to, so retirement can
        // derive, and therefore declare, the keys it must delete. Since 025 the claimant IS
        // the group (no longer "a group IS a node"), so this is keyed by group.
        AddKey(CreateArgSlice(HighwayKeys.NodeChannels(_group)), LockType.Exclusive, StoreType.Main);

        // 025: membership — the nodes backing this group — and the node's own reverse index,
        // which is what BYE PURGE walks now that a node can back groups not named after it.
        AddKey(CreateArgSlice(HighwayKeys.GroupMembers(_channel, _group)), LockType.Exclusive, StoreType.Main);
        AddKey(CreateArgSlice(HighwayKeys.NodeSubs(_node)), LockType.Exclusive, StoreType.Main);
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

            AddToMirrorList(api, HighwayKeys.NodeChannels(_group), _channel);

            // 025: membership, in both directions. Idempotent like every mirror write.
            AddToMirrorList(api, HighwayKeys.GroupMembers(_channel, _group), _node);
            AddToMirrorList(api, HighwayKeys.NodeSubs(_node), $"{_channel}@{_group}");

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
