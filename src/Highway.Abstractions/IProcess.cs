namespace Highway.Abstractions;

/// <summary>
/// Implement this to process queued messages of type <typeparamref name="T"/> (feature 014).
///
/// <para><b>Exactly one implementation per message type.</b> Two processors for the same
/// message is an error at startup, not a fan-out — fan-out is what <c>PublishAsync</c> and
/// <see cref="ISubscribe{T}"/> are for.</para>
///
/// <para>Multiple <i>instances</i> of the same processor compete: deploy three copies of the
/// application and they share the work rather than each receiving a copy.</para>
///
/// <para>Delivery is <b>at least once</b>. If this method completes but the acknowledgement
/// is lost, the message is delivered again. Mark the message
/// <c>[Idempotent]</c> to have Highway suppress that redelivery.</para>
/// </summary>
public interface IProcess<in T> where T : ISend
{
    Task ProcessAsync(T message, CancellationToken ct = default);
}
