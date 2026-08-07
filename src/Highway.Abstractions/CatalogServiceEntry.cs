using System.Text.Json.Serialization;

namespace Highway.Abstractions;

/// <summary>
/// A single service entry in the catalog — sent as part of heartbeat.
/// </summary>
public sealed class CatalogServiceEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("requestType")]
    public required string RequestTypeName { get; init; }

    [JsonPropertyName("responseType")]
    public required string ResponseTypeName { get; init; }
}
