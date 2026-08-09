namespace Highway.Abstractions;

/// <summary>
/// Result of a PublishAsync call — reports how many subscribers received the message.
///
/// <para><b>Not returned on the delivery path (018).</b> A subscriber worker discards this: a
/// delivery that had failures now throws instead of reporting a count, so the failure reaches
/// the dead letter with its exception intact. <see cref="SuccessCalls"/> is only meaningful to
/// a caller invoking the executor directly.</para>
/// </summary>
public sealed class ChannelResponse
{
    /// <summary>
    /// Total number of subscriber groups the message was delivered to.
    /// </summary>
    public required int TotalSubscribers { get; init; }

    /// <summary>
    /// Number of subscribers that processed the message successfully.
    ///
    /// <para>On the delivery path this is always equal to <see cref="TotalSubscribers"/>,
    /// because anything less throws. It can only differ for a direct caller that catches.</para>
    /// </summary>
    public required int SuccessCalls { get; init; }
}
