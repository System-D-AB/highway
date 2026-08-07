using System.Text.Json;

namespace Highway.Client.Wire;

/// <summary>
/// Versioned JSON wrapper around every user payload on the wire
/// (<c>HW.CALL</c>, <c>HW.REPLY</c>, <c>HW.PUBLISH</c>):
/// <code>{ "v": 1, "src": "&lt;nodeId&gt;", "ts": "&lt;ISO-8601 UTC&gt;", "body": &lt;serialized DTO&gt; }</code>
/// The <c>src</c>/<c>ts</c> headers are the audit/tracing hooks feature 002 builds on.
/// </summary>
internal sealed class HighwayEnvelope
{
    public const int Version = 1;

    /// <summary>Sending node's NodeName.</summary>
    public required string Src { get; init; }

    /// <summary>Send timestamp, ISO-8601 UTC.</summary>
    public required string Ts { get; init; }

    /// <summary>The DTO's own JSON, embedded as a nested object.</summary>
    public required JsonElement Body { get; init; }

    /// <summary>
    /// Optional W3C traceparent (feature 002), so a server-side span joins the
    /// caller's trace. Absent when nothing is being traced. Adding it did not
    /// change the envelope version: readers ignore properties they do not
    /// recognise, verified in both directions.
    /// </summary>
    public string? TraceParent { get; init; }
}

/// <summary>
/// Envelope (de)serialization with the single shared <see cref="JsonSerializerOptions"/>.
/// No polymorphic type metadata is ever written — the wire carries a service/channel
/// name and a JSON shape, never CLR type identity.
/// </summary>
internal static class HighwayJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Serializes a DTO into a wire envelope.</summary>
    public static byte[] EncodeEnvelope(string srcNodeId, object? body, string? traceParent = null)
    {
        var envelope = new System.Text.Json.Nodes.JsonObject
        {
            ["v"] = HighwayEnvelope.Version,
            ["src"] = srcNodeId,
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["body"] = JsonSerializer.SerializeToNode(body, SerializerOptions),
        };

        // Only present when something is actually being traced, so an untraced
        // system pays nothing for the field.
        if (traceParent is not null)
            envelope["tp"] = traceParent;

        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    /// <summary>
    /// Parses a wire envelope. Throws <see cref="JsonException"/> on malformed
    /// input or an unknown version — callers map that to error data (502 for
    /// replies, 400 reply + ACK for requests, log + RACK for pub/sub).
    /// </summary>
    public static HighwayEnvelope DecodeEnvelope(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;

        if (!root.TryGetProperty("v", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != HighwayEnvelope.Version)
        {
            throw new JsonException("Envelope has a missing or unsupported version ('v').");
        }

        if (!root.TryGetProperty("body", out var body))
            throw new JsonException("Envelope is missing the 'body' element.");

        return new HighwayEnvelope
        {
            Src = root.TryGetProperty("src", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() ?? "" : "",
            Ts = root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() ?? "" : "",
            Body = body.Clone(), // Clone: the JsonDocument is disposed when this method returns
            TraceParent = root.TryGetProperty("tp", out var tp) && tp.ValueKind == JsonValueKind.String
                ? tp.GetString()
                : null,
        };
    }

    /// <summary>Deserializes an envelope body into <paramref name="type"/>.</summary>
    public static object? DeserializeBody(HighwayEnvelope envelope, Type type)
        => JsonSerializer.Deserialize(envelope.Body, type, SerializerOptions);
}
