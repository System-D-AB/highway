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
    /// Send work to a queue, to be handled by <b>exactly one</b> <see cref="IProcess{T}"/>
    /// (feature 014). Returns the message id.
    ///
    /// <para><b>Send or Publish?</b> One handler → Send. Many handlers → Publish. Need the
    /// answer → Execute. Three instances of a processor <i>share</i> the work; three
    /// instances of a subscriber each get <i>their own copy</i>.</para>
    ///
    /// <para><b>Sending never requires a running processor.</b> The message waits in the
    /// queue until one claims it.</para>
    ///
    /// <para>The id is returned because the first thing anyone wants when a queued job
    /// misbehaves is to find it in the dead-letter queue, and <c>HW.DLQ PEEK</c> reports
    /// entries by id. Keep it if you will want to.</para>
    /// </summary>
    Task<string> SendAsync(ISend message, CancellationToken ct = default);

    /// <summary>
    /// Send work that must not be processed before <paramref name="delay"/> has elapsed.
    ///
    /// <para>A "not before", not an alarm clock: delivery is driven by workers polling, so
    /// the message is claimed on the first poll after its time. A non-positive delay sends
    /// immediately.</para>
    /// </summary>
    Task<string> SendAsync(ISend message, TimeSpan delay, CancellationToken ct = default);

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
