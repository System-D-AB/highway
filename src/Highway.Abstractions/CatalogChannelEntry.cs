using System.Text.Json.Serialization;

namespace Highway.Abstractions;

/// <summary>
/// A single channel entry in the catalog — sent as part of heartbeat.
/// </summary>
public sealed class CatalogChannelEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("subscriberCount")]
    public required int SubscriberCount { get; init; }
}
