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
