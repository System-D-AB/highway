using System.Text.Json.Serialization;

namespace Highway.Abstractions;

/// <summary>
/// Serializable catalog summary sent via HW.HEARTBEAT.
/// Contains the services, queues and channels this node offers.
/// </summary>
public sealed class CatalogInfo
{
    [JsonPropertyName("services")]
    public required IReadOnlyList<CatalogServiceEntry> Services { get; init; }

    [JsonPropertyName("channels")]
    public required IReadOnlyList<CatalogChannelEntry> Channels { get; init; }

    /// <summary>
    /// Queues this node processes (feature 014). Optional for backward compatibility: a
    /// catalog written before queues existed omits it, and must still deserialize.
    /// </summary>
    [JsonPropertyName("queues")]
    public IReadOnlyList<CatalogQueueEntry> Queues { get; init; } = [];
}
