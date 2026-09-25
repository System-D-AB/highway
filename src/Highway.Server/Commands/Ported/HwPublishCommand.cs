using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.PUBLISH &lt;channel&gt; &lt;payload&gt; [AT &lt;ticks&gt;] → :N (groups delivered to).
///
/// Fans a message out to every subscriber group's derived queue (<c>{channel}@{group}</c>),
/// or into each group's delayed set when <c>AT</c> defers it. Allocates a per-channel monotone
/// message id from the channel seq counter, enforces the per-group byte budget atomically
/// (all-or-none fan-out, 018), and rings each group's doorbell post-commit.
///
/// <para>Ported from Garnet. Two model changes: group membership is read directly from the
/// <c>s</c>-family set via <see cref="IHighwayStore.SetMembers"/> (the <c>grplist</c> mirror is
/// gone — T3.3 collapse, and this command is where the collapse first shows), and the clock is
/// <see cref="CommandContext.NowTicks"/>. <b>Auto-retirement of dead groups</b> rides here too
/// (017, completed in T7): a group whose every backing node's heartbeat has gone stale past
/// <see cref="HighwayServerOptions.SubscriberRetirementThreshold"/> is retired before the budget
/// check, so the publish that would have been blocked by a dead subscriber is the one that
/// clears it — and a group past half the threshold is recorded <c>NodeSuspect</c>.</para>
/// </summary>
internal sealed class HwPublishCommand : HighwayCommand
{
    private string _channel = null!;
    private byte[] _payloadBytes = [];
    private long _deliverAtTicks;

    private string[] _groups = [];
    private long _messageId;
    private int _delivered;
    private string? _refusedReason;

    // Auto-retirement accounting for the recorder (017). Loud because retirement is the largest
    // single loss Highway can inflict (C4.3).
    private string[] _suspectGroups = [];
    private int _retiredGroups;
    private long _retiredMessages;
    private long _retiredBytes;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "channel", ctx.Options.MaxIdentifierBytes, out _channel))
            return false;
        if (!TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _payloadBytes))
            return false;

        var keyword = input.Next(ref idx);
        if (keyword.Length > 0)
        {
            var word = Encoding.ASCII.GetString(keyword).ToUpperInvariant();
            if (word != "AT")
                return Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'; expected AT");

            var value = input.Next(ref idx);
            if (value.Length == 0
                || !long.TryParse(Encoding.ASCII.GetString(value), out var ticks)
                || ticks < 0)
                return Fail(HighwayErrors.InvalidArg, "AT requires a non-negative .NET UTC tick count");

            _deliverAtTicks = ticks;
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        // 065: every group queue is its own box. Resolve them all first (catalog creation takes its own
        // lock), then hold the channel and every group box together — in stripe order — so the fan-out is
        // atomic and serializes against claims on those groups (before 3.0 publish locked only the channel
        // while claims locked the group queue, and the two could race on the queue's sequence).
        string[] groups;
        using (var snap = store.Snapshot())
            groups = ReadGroupNames(store, snap, _channel);
        var boxes = groups.ToDictionary(g => g, g => BoxCommandSupport.Ensure(ctx, BoxKeys.Kind.Queue, BoxCommandSupport.GroupQueue(_channel, g)));
        using var _lock = ctx.Locks.LockMany([_channel, .. boxes.Values.Select(WorkBox.LockName)]);

        using var snapshot = store.Snapshot();
        _groups = ReadGroupNames(store, snapshot, _channel).Where(boxes.ContainsKey).ToArray();   // a group added since has no lock held: next publish
        using var batch = store.NewBatch();
        _messageId = store.Increment(batch, HighwayKeyspace.Counter(HighwayNames.ChannelSeq(_channel)), 1);
        var messageIdBytes = Encoding.UTF8.GetBytes(_messageId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var entry = Envelope.EncodeRpcEntry(messageIdBytes, _payloadBytes);

        var retired = new List<(BoxKeys.Kind, string, uint)>();
        if (_groups.Length > 0 && ctx.Options.SubscriberRetirementThreshold > TimeSpan.Zero)
            ClassifyAndRetireGroups(ctx, snapshot, batch, retired);

        if (_groups.Length == 0)
        {
            batch.Commit();
            BoxCommandSupport.Forget(ctx, retired);
            _delivered = 0;
            writer.Integer(0);
            return;
        }

        var targets = _groups.Select(g => BoxCommandSupport.Open(ctx, boxes[g], BoxKeys.Kind.Queue, batch)).ToList();
        try
        {
            if (ctx.Options.MaxQueueBytes > 0)
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    var used = targets[i].Bytes;
                    if (used + entry.Length <= ctx.Options.MaxQueueBytes) continue;
                    _refusedReason = $"group '{_groups[i]}' at {used}/{ctx.Options.MaxQueueBytes} bytes";
                    writer.Error(HighwayErrors.Format(
                        HighwayErrors.QueueFull,
                        $"channel '{_channel}' refused: group '{_groups[i]}' is at its limit " +
                        $"({used} of {ctx.Options.MaxQueueBytes} bytes). No group received this " +
                        "message - a publish reaches every registered group or none."));
                    return;   // batch disposed uncommitted → nothing written
                }
            }

            var deferred = _deliverAtTicks > ctx.NowTicks;
            foreach (var wb in targets)
            {
                if (deferred) wb.EnqueueDelayed(entry, _deliverAtTicks);
                else wb.Enqueue(entry);
            }
            batch.Commit();
            foreach (var wb in targets) wb.AfterCommit();
            BoxCommandSupport.Forget(ctx, retired);

            // A deferred publish has reached no group yet; it counts on promotion.
            _delivered = deferred ? 0 : _groups.Length;
            writer.Integer(_delivered);
        }
        finally
        {
            foreach (var wb in targets) wb.Dispose();
        }
    }

    /// <summary>
    /// Subscriber retirement (017): a group all of whose registered members have been silent past the
    /// threshold is destroyed here, before the fan-out, and does not receive the message; one past half the
    /// threshold is reported as suspect. A member with no registration record is not evidence of death.
    /// </summary>
    private void ClassifyAndRetireGroups(CommandContext ctx, IStoreSnapshot snap, IStoreBatch batch, List<(BoxKeys.Kind, string, uint)> retired)
    {
        var store = ctx.Store;
        var threshold = ctx.Options.SubscriberRetirementThreshold;
        var halfThreshold = threshold / 2;
        var dead = new List<string>();
        var suspect = new List<string>();

        foreach (var group in _groups)
        {
            var members = RegistrySupport.ReadSet(store, snap, HighwayNames.GroupMembers(_channel, group));
            if (members.Length == 0)
                members = [group]; // pre-025: the group is the node

            var anyRecord = false;
            var allStale = true;
            var allPastHalf = true;
            foreach (var member in members)
            {
                var record = store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(member)));
                if (record is null || record.Length < NodeRegistration.HeaderSize)
                    continue;
                anyRecord = true;
                var seen = RegistrySupport.SeenTicks(store, snap, member, record);
                if (!NodeRegistration.IsStale(seen, ctx.NowTicks, threshold)) allStale = false;
                if (!NodeRegistration.IsStale(seen, ctx.NowTicks, halfThreshold)) allPastHalf = false;
            }

            if (anyRecord && allStale) dead.Add(group);
            else if (anyRecord && allPastHalf) suspect.Add(group);
        }

        _suspectGroups = [.. suspect];
        foreach (var group in dead)
        {
            var destroyed = BoxCommandSupport.RetireGroup(ctx, batch, _channel, group, retired);
            _retiredGroups++;
            _retiredMessages += destroyed.Messages;
            _retiredBytes += destroyed.Bytes;
        }
        if (dead.Count > 0)
            _groups = [.. _groups.Where(g => !dead.Contains(g))];
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_refusedReason is not null)
        {
            ctx.Recorder.Record(HighwayEventType.SendRefused, _channel, errorCode: _refusedReason);
            return; // refused: no Published record, no doorbell
        }

        // Loud, and in the replay (017 T7): "where did my subscriber's backlog go?" must be
        // answerable without guessing.
        foreach (var group in _suspectGroups)
        {
            ctx.Recorder.Record(
                HighwayEventType.NodeSuspect, _channel,
                nodeId: group,
                errorCode: $"node '{group}' has been absent past half the retirement threshold; " +
                           "its subscriber queue will be destroyed if it does not return");
        }

        if (_retiredGroups > 0)
        {
            ctx.Recorder.Record(
                HighwayEventType.GroupRetired, _channel,
                count: (int)_retiredMessages,
                errorCode: $"retired {_retiredGroups} group(s), discarded {_retiredMessages} message(s) / {_retiredBytes} byte(s)");
        }

        ctx.Recorder.Record(
            HighwayEventType.Published, _channel,
            messageId: _messageId == 0 ? null : _messageId,
            payload: _payloadBytes,
            errorCode: FailureCode,
            count: _delivered);

        if (Failed) return;

        // A deferred message wakes nobody until promoted.
        if (_deliverAtTicks > ctx.NowTicks) return;

        var msgIdBytes = Encoding.UTF8.GetBytes(_messageId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var group in _groups)
            ctx.Doorbell.Ring(HighwayKeys.QueueDoorbell($"{_channel}@{group}"), msgIdBytes);
    }

    /// <summary>Reads the channel's group names from the membership set (no mirror — T3.3).</summary>
    private static string[] ReadGroupNames(IHighwayStore store, IStoreSnapshot snap, string channel)
    {
        var members = store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.ChannelGroups(channel)));
        return [.. members.Select(m => Encoding.UTF8.GetString(m))];
    }
}
