using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
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

        using var _lock = ctx.Locks.Lock(_channel);
        using var snap = store.Snapshot();

        // Group membership straight from the set — no mirror (T3.3). Members are the group names.
        _groups = ReadGroupNames(store, snap, _channel);

        using var batch = store.NewBatch();

        // Per-channel monotone message id (the only Increment on the channel seq).
        _messageId = store.Increment(batch, HighwayKeyspace.Counter(HighwayNames.ChannelSeq(_channel)), 1);
        var messageIdBytes = Encoding.UTF8.GetBytes(_messageId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var rpcEntry = Envelope.EncodeRpcEntry(messageIdBytes, _payloadBytes);

        if (_deliverAtTicks > ctx.NowTicks)
        {
            // Deferred: fan out into each group's delayed set. Delivered-now count is 0.
            foreach (var group in _groups)
            {
                var derived = $"{_channel}@{group}";
                store.SortedSetAdd(batch, HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed(derived)), _deliverAtTicks, rpcEntry);
            }
            batch.Commit();
            _delivered = 0;
            writer.Integer(0);
            return;
        }

        if (_groups.Length == 0)
        {
            // Nobody subscribed — delivered to nobody. That is what publish means.
            batch.Commit();
            _delivered = 0;
            writer.Integer(0);
            return;
        }

        // Automatic retirement (017) rides on the publish: it already reads this channel's
        // groups and is about to write to each. A group whose every backing node has stopped
        // heartbeating past the threshold is retired first, so a dead subscriber cannot fail
        // the budget check below for the living ones. Liveness evidence (member heartbeats),
        // never a consumption gap — a group nobody consumed from is not dead.
        if (ctx.Options.SubscriberRetirementThreshold > TimeSpan.Zero)
            ClassifyAndRetireGroups(ctx, store, snap, batch);

        if (_groups.Length == 0)
        {
            // Every group was just retired — the message reaches nobody now.
            batch.Commit();
            _delivered = 0;
            writer.Integer(0);
            return;
        }

        // Check EVERY group's budget before writing ANY (018: all-or-none fan-out).
        if (ctx.Options.MaxQueueBytes > 0)
        {
            foreach (var group in _groups)
            {
                var derived = $"{_channel}@{group}";
                var used = store.ReadByteCounter(snap, HighwayNames.QueueBytes(derived));
                if (used + rpcEntry.Length > ctx.Options.MaxQueueBytes)
                {
                    _refusedReason = $"group '{group}' at {used}/{ctx.Options.MaxQueueBytes} bytes";
                    writer.Error(HighwayErrors.Format(
                        HighwayErrors.QueueFull,
                        $"channel '{_channel}' refused: group '{group}' is at its limit " +
                        $"({used} of {ctx.Options.MaxQueueBytes} bytes). No group received this " +
                        "message - a publish reaches every registered group or none."));
                    return; // batch disposed uncommitted → nothing written
                }
            }
        }

        foreach (var group in _groups)
        {
            var derived = $"{_channel}@{group}";
            HwQSendCommand.PushTail(store, batch, HighwayNames.Queue(derived), rpcEntry);
            store.AdjustByteCounter(batch, HighwayNames.QueueBytes(derived), rpcEntry.Length);
        }

        batch.Commit();
        _delivered = _groups.Length;
        writer.Integer(_groups.Length);
    }

    /// <summary>
    /// Classifies each group by its members' heartbeat staleness and retires the dead ones in
    /// the caller's batch (017). A member with no registration record is NOT evidence of death
    /// (a subscriber may never have registered a catalog); only a record that exists and has
    /// gone stale counts. Groups past half the threshold become <see cref="_suspectGroups"/>.
    /// Members come from the <c>grp:members</c> set (was a mirror); a group with no membership
    /// predates 025 and its name is a node name (the 017 fallback).
    /// </summary>
    private void ClassifyAndRetireGroups(CommandContext ctx, IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch)
    {
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
                    continue; // never registered — not evidence of death

                anyRecord = true;
                if (!NodeRegistration.IsStale(record, ctx.NowTicks, threshold)) allStale = false;
                if (!NodeRegistration.IsStale(record, ctx.NowTicks, halfThreshold)) allPastHalf = false;
            }

            if (anyRecord && allStale) dead.Add(group);
            else if (anyRecord && allPastHalf) suspect.Add(group);
        }

        _suspectGroups = [.. suspect];

        foreach (var group in dead)
        {
            var destroyed = RegistrySupport.RetireGroup(store, snap, batch, _channel, group);
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
