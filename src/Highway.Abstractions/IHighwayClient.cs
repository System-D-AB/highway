namespace Highway.Abstractions;

/// <summary>
/// The primary client interface for Highway — provides RPC and Pub/Sub verbs.
/// </summary>
public interface IHighwayClient
{
    /// <summary>
    /// Execute a remote procedure call. Returns the typed response or an error via Output.StatusCode.
    /// </summary>
    Task<TResponse> ExecuteAsync<TResponse>(IReturn<TResponse> request, CancellationToken ct = default)
        where TResponse : Output;

    /// <summary>
    /// Publish a message to all subscribers of the associated channel.
    /// </summary>
    Task PublishAsync(IPublish message, CancellationToken ct = default);

    /// <summary>
    /// Publish a message that must not be delivered before <paramref name="delay"/> has
    /// elapsed (feature 013).
    ///
    /// <para><b>This is a "not before", not an alarm clock.</b> Delivery is driven by
    /// consumers polling, not by a timer inside the broker, so the message arrives on the
    /// first poll after its delivery time — in a running system, within the consumer's
    /// backstop interval; in a channel whose subscribers are all stopped, not until one
    /// starts. If you need second-accurate execution with nothing running, you need a
    /// scheduler, and Highway is not one.</para>
    ///
    /// <para>Groups are resolved at <i>delivery</i> time, so a subscriber that registers
    /// during the delay still receives the message: a delayed publish behaves like a
    /// publish that happens later.</para>
    ///
    /// <para>A delay of zero or less publishes immediately. There is no way to cancel or
    /// list a pending delayed message.</para>
    /// </summary>
    Task PublishAsync(IPublish message, TimeSpan delay, CancellationToken ct = default);
}
