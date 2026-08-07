namespace Highway.Client.Scanning;

/// <summary>
/// Immutable metadata about a discovered channel.
/// </summary>
public sealed class ChannelDescriptor
{
    /// <summary>Channel name from [Channel] attribute.</summary>
    public required string Name { get; init; }

    /// <summary>The message type (T) implementing IPublish.</summary>
    public required Type MessageType { get; init; }

    /// <summary>All subscriber descriptors for this channel.</summary>
    public required IReadOnlyList<SubscriberDescriptor> Subscribers { get; init; }

    /// <summary>Whether this is an internal Highway channel.</summary>
    public bool IsInternal { get; init; }
}
