using System.Text.Json.Serialization;

namespace Highway.Abstractions.Observability;

/// <summary>
/// One recorded operation in the flight recorder (feature 002).
///
/// <para><b>Identifiers match the wire protocol, not CLR convenience.</b> A
/// request ID is an opaque identifier <em>string</em> — the .NET client happens
/// to generate a GUID in "N" format, but nothing in the protocol requires that
/// and another client may use anything. A message ID is a <em>long</em>: the
/// per-channel sequence number <c>HW.PUBLISH</c> assigns.</para>
///
/// <para>Events are held as objects in memory and serialized only when
/// <c>HW.REPLAY</c> reads them, so nothing on this type is serialized on the
/// recording path.</para>
/// </summary>
public sealed class HighwayEvent
{
    /// <summary>Current schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>When the operation completed, UTC.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>What happened.</summary>
    [JsonPropertyName("eventType")]
    public required HighwayEventType EventType { get; init; }

    /// <summary>Service or channel name. Recording is configured per name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The node involved, where the command carried one.</summary>
    [JsonPropertyName("nodeId")]
    public string? NodeId { get; init; }

    /// <summary>
    /// RPC correlation handle. An opaque identifier string, not a GUID —
    /// the protocol places no format requirement on it.
    /// </summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>Pub/sub message ID: the per-channel sequence number, not a GUID.</summary>
    [JsonPropertyName("messageId")]
    public long? MessageId { get; init; }

    /// <summary>
    /// Payload bytes, present only when the name's capture mode is
    /// <see cref="PayloadCapture.Full"/>. Under <c>HeadersOnly</c> this is null
    /// and <see cref="PayloadSize"/> still reports the real size.
    /// </summary>
    [JsonPropertyName("payload")]
    public byte[]? Payload { get; init; }

    /// <summary>Payload size in bytes. Always populated, whatever the capture mode.</summary>
    [JsonPropertyName("payloadSize")]
    public int PayloadSize { get; init; }

    /// <summary>
    /// The Highway error code when the command was rejected — for example
    /// <c>HW_INVALID_ARG</c>.
    ///
    /// <para>This exists because a command rejected during validation never
    /// produces an <see cref="Output"/>, so <see cref="StatusCode"/> alone
    /// cannot represent it. Failures are the most valuable thing in a flight
    /// recorder; without this field the recorder could not show them.</para>
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Client-facing HTTP-style status, where the operation produced one.
    /// Server-side command rejections use <see cref="ErrorCode"/> instead.
    /// </summary>
    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; init; }

    /// <summary>Count reported by the operation — group count for a publish, batch size for a receive.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; init; }

    /// <summary>Schema version, so readers can cope with later additions.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Approximate retained size in bytes, used for the recorder's memory
    /// accounting. Deliberately an estimate: exact object graph measurement
    /// would cost more than the budget it protects.
    /// </summary>
    [JsonIgnore]
    public int ApproximateBytes =>
        128                                     // object header, fields, timestamp
        + (Name.Length * 2)
        + ((NodeId?.Length ?? 0) * 2)
        + ((RequestId?.Length ?? 0) * 2)
        + ((ErrorCode?.Length ?? 0) * 2)
        + (Payload?.Length ?? 0);
}
