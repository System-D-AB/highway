namespace Highway.Abstractions;

/// <summary>
/// Result of a PublishAsync call — reports how many subscribers received the message.
/// </summary>
public sealed class ChannelResponse
{
    /// <summary>
    /// Total number of subscriber groups the message was delivered to.
    /// </summary>
    public required int TotalSubscribers { get; init; }

    /// <summary>
    /// Number of subscribers that processed the message successfully.
    /// If SuccessCalls &lt; TotalSubscribers, at least one subscriber faulted.
    /// </summary>
    public required int SuccessCalls { get; init; }
}
