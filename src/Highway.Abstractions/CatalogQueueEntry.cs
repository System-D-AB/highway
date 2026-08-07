using System.Text.Json.Serialization;

namespace Highway.Abstractions;

/// <summary>
/// A single queue entry in the catalog (feature 014).
///
/// <para>Distinct from a service entry because a queue has <b>no response type</b> — the
/// sender is not waiting for one. Reporting queues as services would make
/// <c>HW.DISCOVER</c> and the dashboard misleading.</para>
/// </summary>
public sealed class CatalogQueueEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("messageType")]
    public required string MessageTypeName { get; init; }
}
