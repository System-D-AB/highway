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

    /// <summary>
    /// Routes this node can address because it references their contracts (feature 024).
    /// <b>Addressability, not proof of calling.</b> Optional for backward compatibility: a
    /// pre-024 catalog omits it and must still deserialize.
    /// </summary>
    [JsonPropertyName("uses")]
    public CatalogUses? Uses { get; init; }
}

/// <summary>The can-use half of a node's catalog (feature 024). Route names only.</summary>
public sealed class CatalogUses
{
    [JsonPropertyName("services")]
    public IReadOnlyList<string> Services { get; init; } = [];

    [JsonPropertyName("queues")]
    public IReadOnlyList<string> Queues { get; init; } = [];

    [JsonPropertyName("channels")]
    public IReadOnlyList<string> Channels { get; init; } = [];
}
