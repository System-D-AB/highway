using Highway.Abstractions.Observability;
using Highway.Server.Observability;

namespace Highway.Server.Dashboard;

internal sealed record EventDto(
    DateTimeOffset Timestamp,
    string Type,
    string Name,
    string? Node,
    string? RequestId,
    long? MessageId,
    int PayloadSize,
    string? Payload,       // base64 when captured, null otherwise
    string PayloadState,   // "captured" | "headers-only" | "not-captured" | "disabled"
    string? ErrorCode,
    int? Count);

internal sealed record RecorderDto(
    string Broker,
    bool Enabled,
    bool ReplayEnabled,
    IReadOnlyList<NameDto> Names,
    long TotalEvents,
    long TotalBytes,
    long DroppedCapacity,
    long DroppedBudget,
    long Failures,
    long ObserverFailures);

internal sealed record NameDto(
    string Name, int Count, long Bytes, string Capture, long DroppedCapacity);

/// <summary>
/// Single projection enforcing capture mode and ReplayEnabled for both query and stream paths.
/// </summary>
internal static class EventProjection
{
    public static EventDto Project(HighwayEvent evt, PayloadCapture capture, bool replayEnabled)
    {
        string? payload = null;
        string payloadState;

        if (!replayEnabled)
        {
            payloadState = "disabled";
        }
        else if (capture == PayloadCapture.Off)
        {
            payloadState = "not-captured";
        }
        else if (capture == PayloadCapture.HeadersOnly)
        {
            payloadState = "headers-only";
        }
        else if (evt.Payload is not null)
        {
            payload = Convert.ToBase64String(evt.Payload);
            payloadState = "captured";
        }
        else
        {
            payloadState = evt.PayloadSize > 0 ? "not-captured" : "captured"; // empty payload is still "captured"
        }

        return new EventDto(
            evt.Timestamp,
            evt.EventType.ToString(),
            evt.Name,
            evt.NodeId,
            evt.RequestId,
            evt.MessageId,
            evt.PayloadSize,
            payload,
            payloadState,
            evt.ErrorCode,
            evt.Count);
    }
}


// ---- feature 022: the catalogue -------------------------------------------

/// <summary>
/// One catalogue row. <c>Kind</c> and <c>State</c> are decided by the server — the browser
/// never parses a name, because <c>@</c> is a separator the server derives (018 T0).
/// </summary>
internal sealed record CatalogueRowDto(
    string Name,
    string Kind,
    string State,
    string? ParentChannel,
    IReadOnlyList<string> Hosts,
    long? Depth,
    long? Bytes,
    long? MaxBytes,
    long? DeadLettered);

internal sealed record CatalogueDto(
    IReadOnlyList<CatalogueRowDto> Entries,
    string? Unavailable);

/// <summary>One node, with liveness already interpreted so the browser does no arithmetic.</summary>
internal sealed record NodeRowDto(
    string Name,
    string State,          // "live" | "stale" | "absent"
    double SinceSeconds,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Queues,
    IReadOnlyList<string> Channels,

    /// <summary>Peer address of the node's live connection, or null if it has none right now.</summary>
    string? SeenFrom);

internal sealed record NodesDto(
    IReadOnlyList<NodeRowDto> Nodes,
    string? Unavailable);


// ---- feature 023: messages -------------------------------------------------

/// <summary>
/// One message as a developer thinks of it. <b>Both nodes</b>, because a message is usually
/// produced on one and processed on another.
/// </summary>
internal sealed record MessageRowDto(
    string Id,
    string Outcome,
    DateTimeOffset? StartedAt,
    string? StartedOnNode,
    DateTimeOffset? CompletedAt,
    string? CompletedOnNode,
    double? DurationMs,
    string? FailureDetail,

    /// <summary>Fan-out, for a channel: groups finished out of groups subscribed. Null otherwise.</summary>
    int? DeliveredGroups,
    int? SubscriberGroups);

internal sealed record MessageListDto(
    IReadOnlyList<MessageRowDto> Messages,
    int Processed,
    int Failed,
    int DeadLettered,
    int Refused,
    int InFlight,
    DateTimeOffset? WindowStart);

internal sealed record MessageStepRowDto(
    DateTimeOffset At,
    string Type,
    string Visibility,
    string? Node,
    double? SincePreviousMs,
    string? Detail);

/// <summary>
/// One message's whole life. The body loads here and not in the list (023 R7.4): a list that
/// ships every payload it names is a dashboard that becomes its own broker's heaviest client.
/// </summary>
internal sealed record MessageDetailDto(
    string Id,
    string Entity,
    string Outcome,
    IReadOnlyList<MessageStepRowDto> Steps,
    string? Payload,
    string PayloadState);

// ---- feature 023 T6: one node's work ---------------------------------------

/// <summary>A message row that names its entity, because a node's list spans all of them.</summary>
internal sealed record NodeMessageRowDto(
    string Entity,
    string Id,
    string Outcome,
    DateTimeOffset? CompletedAt,
    double? DurationMs,
    string? FailureDetail);

/// <summary>
/// One node: what it declared, where it is seen from, and what it has actually done.
///
/// <para>The declared list and the message list answer different questions. "Hosts orders.create"
/// is a claim the node made; "processed 41 orders.create messages" is a thing that happened. A
/// node that declares a service and has processed nothing through it is the exact shape of a
/// misconfiguration, and it is only visible when both are on the same page.</para>
/// </summary>
internal sealed record NodeDetailDto(
    string Name,
    string State,
    double SinceSeconds,
    string? SeenFrom,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Queues,
    IReadOnlyList<string> Channels,
    IReadOnlyList<NodeMessageRowDto> Messages,
    int Processed,
    int Failed,
    string? Unavailable,

    /// <summary>Reference-derived addressability (024) — labelled "can use", never "uses".</summary>
    NodeCanUseDto? CanUse = null);

internal sealed record NodeCanUseDto(
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Queues,
    IReadOnlyList<string> Channels);
