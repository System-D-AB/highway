using System.Text;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// Retiring a subscriber group and everything it owns (feature 017).
///
/// <para><b>One implementation, three callers</b> — <c>HW.UNSUBSCRIBE</c>, <c>HW.HEARTBEAT BYE
/// PURGE</c>, and the automatic retirement that rides on the heartbeat prune. Feature 013 found
/// one bug living in three independently written requeue paths and 015 found a failure block
/// dropped at three of four re-encode sites; a retirement written once cannot diverge the way
/// those did.</para>
/// </summary>
internal abstract partial class HighwayCommandBase
{
    /// <summary>What a retirement destroyed, so an irreversible act leaves a record.</summary>
    internal readonly record struct RetirementOutcome(int Groups, long Messages, long Bytes)
    {
        public static RetirementOutcome operator +(RetirementOutcome a, RetirementOutcome b)
            => new(a.Groups + b.Groups, a.Messages + b.Messages, a.Bytes + b.Bytes);
    }

    /// <summary>
    /// Every key a group's queue owns. Retirement and <c>HW.UNSUBSCRIBE</c> both need the full
    /// list, and both need it declarable in <c>Prepare</c> — so it is derived from the two
    /// names and nothing else.
    ///
    /// <para>Object-store keys are returned separately from main-store ones because Garnet
    /// locks them differently, and declaring one as the other fails at run time rather than
    /// compile time.</para>
    /// </summary>
    protected static (string[] ObjectKeys, string[] MainKeys) GroupQueueKeys(string channel, string group)
    {
        var derived = $"{channel}@{group}";

        // The processing list is derivable rather than discovered, because the claimant IS
        // the group (018, restated by 025): every replica claims with the group's name, so
        // one shared processing list serves them all. That is what keeps every key
        // declarable in Prepare without reading an object-store set — which would register a watch and fail the exclusive locks (004.1).
        return (
            [
                HighwayKeys.Queue(derived),
                HighwayKeys.QueueDelayed(derived),
                HighwayKeys.QueueDeadLetter(derived),
                HighwayKeys.QueueNodes(derived),
                HighwayKeys.QueueProcessing(derived, group),
            ],
            [
                HighwayKeys.QueueBytes(derived),
                HighwayKeys.QueueNodeList(derived),

                // 025: membership dies with the group -- a retired group has no members.
                HighwayKeys.GroupMembers(channel, group),
            ]);
    }

    /// <summary>
    /// Deletes a subscriber group and its queue, returning what was destroyed.
    ///
    /// <para><b>Deleted, not dead-lettered</b> (017 decision 1). Those messages were addressed
    /// to this subscriber alone: nobody else can process them, and the subscriber has declared
    /// it will never exist again. Moving them to a dead-letter list would preserve a gigabyte
    /// for nobody and would not reclaim the bytes, so the hazard this feature exists to fix
    /// would survive the fix.</para>
    ///
    /// <para>The count comes back so the caller can say what was lost. This is the largest
    /// single loss Highway can inflict, and C4.3's rule — a loss is never silent — applies here
    /// more than anywhere else in the product.</para>
    /// </summary>
    protected RetirementOutcome RetireGroup<TGarnetApi>(TGarnetApi api, string channel, string group)
        where TGarnetApi : IGarnetApi
    {
        var derived = $"{channel}@{group}";
        var queueKey = CreateArgSlice(HighwayKeys.Queue(derived));

        // Counted before deletion: afterwards there is nothing left to count, and "we discarded
        // an unknown number of messages" is not an answer an operator can act on.
        long messages = 0;
        if (api.ListLength(queueKey, out var length) == GarnetStatus.OK)
            messages = length;

        var bytes = ReadByteCounter(api, HighwayKeys.QueueBytes(derived));

        var (objectKeys, mainKeys) = GroupQueueKeys(channel, group);
        foreach (var key in objectKeys) api.DELETE(CreateArgSlice(key));
        foreach (var key in mainKeys) api.DELETE(CreateArgSlice(key));

        // Unregister the group itself, or the next publish fans straight back into a queue
        // that was just deleted and the retirement achieves nothing.
        api.SetRemove(
            CreateArgSlice(HighwayKeys.ChannelGroups(channel)),
            CreateArgSlice(Encoding.UTF8.GetBytes(group)),
            out _);

        RemoveFromMirrorList(api, HighwayKeys.ChannelGroupList(channel), group);
        RemoveFromMirrorList(api, HighwayKeys.NodeChannels(group), channel);

        return new RetirementOutcome(1, messages, bytes);
    }

    /// <summary>
    /// The channels a node subscribes to, from the main-store mirror key.
    ///
    /// <para>Read in <c>Prepare</c>, where the object-store set could not be — reading an
    /// object structure there registers a watch that later exclusive locks fail against
    /// (004.1). This is the whole reason the mirror key exists.</para>
    /// </summary>
    protected string[] ReadNodeChannels<TGarnetReadApi>(TGarnetReadApi api, string nodeId)
        where TGarnetReadApi : IGarnetReadApi
    {
        api.GET(CreateArgSlice(HighwayKeys.NodeChannels(nodeId)), out PinnedSpanByte value);

        return value.Length > 0
            ? Encoding.UTF8.GetString(value.ReadOnlySpan)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];
    }
}
