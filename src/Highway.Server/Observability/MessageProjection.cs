using System.Globalization;
using Highway.Abstractions.Observability;

namespace Highway.Server.Observability;

/// <summary>What became of a message, in words a developer recognises.</summary>
internal enum MessageOutcome
{
    /// <summary>A handler ran and the message was acknowledged.</summary>
    Processed,

    /// <summary>A handler threw (015). It may still be retried.</summary>
    Failed,

    /// <summary>Attempts were exhausted (013). Someone has to act.</summary>
    DeadLettered,

    /// <summary>The broker refused to accept it — a byte limit (016).</summary>
    Refused,

    /// <summary>Sent, not yet finished. Normal for a few seconds; a problem after a few minutes.</summary>
    InFlight,

    /// <summary>
    /// Part of this message's story has aged out of the bounded recorder.
    ///
    /// <para><b>A real outcome, not an error.</b> The recorder is volatile and bounded (002), so
    /// this is the normal state under load — exactly when the view matters most. Reporting it as
    /// anything definite would be a confident lie at the worst possible moment.</para>
    /// </summary>
    Incomplete,
}

/// <summary>One message, as a developer thinks of it.</summary>
internal sealed record MessageSummaryDto(
    string Id,
    string Entity,
    MessageOutcome Outcome,
    DateTimeOffset? StartedAt,
    string? StartedOnNode,
    DateTimeOffset? CompletedAt,
    string? CompletedOnNode,
    double? DurationMs,
    string? FailureDetail,

    /// <summary>
    /// For a channel: how many subscriber groups finished this message, out of how many exist.
    /// Null for anything that does not fan out.
    ///
    /// <para>A single outcome cannot describe a fan-out. Three groups where one failed is not
    /// "failed" and not "processed", and collapsing it to either hides the half that matters.</para>
    /// </summary>
    int? DeliveredGroups = null,
    int? SubscriberGroups = null);

/// <summary>One step in a message's life.</summary>
internal sealed record MessageStepDto(
    DateTimeOffset At,
    string Type,
    EventVisibility Visibility,
    string? Node,
    double? SincePreviousMs,
    string? Detail);

/// <summary>Counts over the recorder's retained window. The window travels with them.</summary>
internal sealed record EntityCountsDto(
    string Entity,
    int Processed,
    int Failed,
    int DeadLettered,
    int Refused,
    int InFlight,
    DateTimeOffset? WindowStart);

/// <summary>
/// Turns protocol events into messages (feature 023).
///
/// <para><b>Why this is server code.</b> A browser doing this would have to fetch every event to
/// group them, re-implement what "acknowledged" means, and get slower exactly as traffic grows.
/// Here it is a scan over buffers the recorder already owns, with one implementation of the
/// protocol's semantics — and it is testable without a browser, which the dashboard's logic has
/// never been.</para>
///
/// <para><b>Correlation needs no new data.</b> Every event already carries the key: RPC and
/// queue events carry <c>RequestId</c>, and a publish carries <c>MessageId</c> — the channel
/// sequence — which is fanned into each group's queue entry as that same number. Three rows
/// appear where one should because nobody grouped them, not because anything is missing.</para>
/// </summary>
internal static class MessageProjection
{
    /// <summary>
    /// The correlation key. <c>Published</c> records a long while group deliveries record the
    /// same value as a string, so both are normalised to text rather than changing what is
    /// recorded — that would be a protocol change for a display problem.
    /// </summary>
    public static string? KeyOf(HighwayEvent e)
        => e.RequestId
           ?? e.MessageId?.ToString(CultureInfo.InvariantCulture);

    /// <param name="subscriberGroups">
    /// How many groups a channel fans out to, or 0 for an entity that does not fan out. The
    /// caller knows this; the events do not, because a group that has received nothing leaves
    /// no trace in them and is exactly the group worth noticing.
    /// </param>
    public static IReadOnlyList<MessageSummaryDto> Summarise(
        string entity, IEnumerable<HighwayEvent> events, int limit = 100, int subscriberGroups = 0)
    {
        var byKey = new Dictionary<string, List<HighwayEvent>>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (KeyOf(e) is not { Length: > 0 } key) continue;

            if (!byKey.TryGetValue(key, out var list))
                byKey[key] = list = [];

            list.Add(e);
        }

        return byKey
            .Select(kv => Summarise(entity, kv.Key, kv.Value, subscriberGroups))
            .OrderByDescending(m => m.StartedAt ?? DateTimeOffset.MinValue)
            .Take(limit)
            .ToArray();
    }

    private static MessageSummaryDto Summarise(
        string entity, string id, List<HighwayEvent> events, int subscriberGroups)
    {
        events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // The START is the first PUBLIC event: what the developer's code did. A claim is the
        // broker starting work, which is a different moment and not the one being reported.
        var start = events.FirstOrDefault(e => EventVisibilityMap.Of(e.EventType) == EventVisibility.Public);

        var (outcome, terminal) = Resolve(events, start);

        var startedAt = start?.Timestamp;
        var completedAt = terminal?.Timestamp;

        return new MessageSummaryDto(
            id,
            entity,
            outcome,
            startedAt,
            start?.NodeId,
            outcome is MessageOutcome.InFlight or MessageOutcome.Incomplete ? null : completedAt,

            // Two nodes, never one: a message is usually produced on one and processed on
            // another, and "shop-1 sent it, order-service-1 processed it" is the sentence the
            // row exists to say.
            outcome is MessageOutcome.InFlight or MessageOutcome.Incomplete ? null : terminal?.NodeId,
            startedAt is { } s && completedAt is { } c && c >= s ? (c - s).TotalMilliseconds : null,
            terminal?.ErrorCode,

            // Each group is resolved on its OWN events, not on the union. A group that failed
            // and a group that succeeded are two facts, and the union has room for one.
            subscriberGroups > 0 ? events
                .Select(e => e.Name)
                .Where(n => !string.Equals(n, entity, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Count(g => Resolve([.. events.Where(e => e.Name == g)], null).Item1
                    == MessageOutcome.Processed)
                : null,
            subscriberGroups > 0 ? subscriberGroups : null);
    }

    /// <summary>
    /// Derives the outcome. Failure and dead-lettering outrank an acknowledgement that preceded
    /// them, because a message can be acknowledged on one attempt and dead-lettered on a later
    /// one, and the later fact is the one that matters.
    /// </summary>
    private static (MessageOutcome, HighwayEvent?) Resolve(List<HighwayEvent> events, HighwayEvent? start)
    {
        HighwayEvent? Last(params HighwayEventType[] types)
            => events.LastOrDefault(e => types.Contains(e.EventType));

        if (Last(HighwayEventType.RpcDeadLettered,
                 HighwayEventType.QueueDeadLettered,
                 HighwayEventType.MessageDeadLettered) is { } dead)
            return (MessageOutcome.DeadLettered, dead);

        if (Last(HighwayEventType.SendRefused) is { } refused)
            return (MessageOutcome.Refused, refused);

        // Acknowledged AFTER a failure means a later attempt succeeded; the reverse means it is
        // still failing. Order decides, not presence.
        var failed = Last(HighwayEventType.DeliveryFailed);
        var done = Last(HighwayEventType.RpcAcknowledged, HighwayEventType.QueueAcknowledged);

        if (failed is not null && (done is null || failed.Timestamp > done.Timestamp))
            return (MessageOutcome.Failed, failed);

        // "Processed" is a PUBLIC fact whose only evidence is an INTERNAL event. The view says
        // "processed"; it never says "acknowledged".
        if (done is not null)
            return (MessageOutcome.Processed, done);

        // Nothing terminal. Either it is still running, or its beginning has aged out of the
        // bounded recorder — and those must not be confused.
        return start is null
            ? (MessageOutcome.Incomplete, null)
            : (MessageOutcome.InFlight, null);
    }

    /// <summary>
    /// One message's steps. <b>Every</b> step is returned, each carrying its visibility, so the
    /// view can lead with the public ones and keep the mechanics one click away — rather than
    /// the server deciding what an operator is allowed to see.
    /// </summary>
    public static IReadOnlyList<MessageStepDto> Timeline(IEnumerable<HighwayEvent> events)
    {
        var ordered = events.OrderBy(e => e.Timestamp).ToArray();
        var steps = new List<MessageStepDto>(ordered.Length);

        for (var i = 0; i < ordered.Length; i++)
        {
            var e = ordered[i];

            steps.Add(new MessageStepDto(
                e.Timestamp,
                e.EventType.ToString(),
                EventVisibilityMap.Of(e.EventType),
                e.NodeId,

                // Durations between steps, not bare clock times: "waited 4.2s in the queue,
                // processed in 30ms" is the diagnosis, two timestamps are its raw material.
                i == 0 ? null : (e.Timestamp - ordered[i - 1].Timestamp).TotalMilliseconds,
                e.ErrorCode));
        }

        return steps;
    }

    /// <summary>
    /// Counts for one entity, with the window they cover. "1,204 processed" reads as a lifetime
    /// total; "1,204 processed since 20:31" is something an operator can act on.
    /// </summary>
    public static EntityCountsDto Count(
        string entity, IEnumerable<HighwayEvent> events, int subscriberGroups = 0)
    {
        var messages = Summarise(entity, events, int.MaxValue, subscriberGroups);
        var window = events.Select(e => e.Timestamp).DefaultIfEmpty().Min();

        return new EntityCountsDto(
            entity,
            messages.Count(m => m.Outcome == MessageOutcome.Processed),
            messages.Count(m => m.Outcome == MessageOutcome.Failed),
            messages.Count(m => m.Outcome == MessageOutcome.DeadLettered),
            messages.Count(m => m.Outcome == MessageOutcome.Refused),
            messages.Count(m => m.Outcome == MessageOutcome.InFlight),
            window == default ? null : window);
    }
}
