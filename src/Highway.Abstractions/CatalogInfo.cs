using System.Text.Json.Serialization;

namespace Highway.Abstractions;

/// <summary>
/// Serializable catalog summary sent via HW.HEARTBEAT.
/// Contains the list of services and channels this node offers.
/// </summary>
public sealed class CatalogInfo
{
    [JsonPropertyName("services")]
    public required IReadOnlyList<CatalogServiceEntry> Services { get; init; }

    [JsonPropertyName("channels")]
    public required IReadOnlyList<CatalogChannelEntry> Channels { get; init; }
}
