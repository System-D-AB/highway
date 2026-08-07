namespace Highway.Abstractions;

/// <summary>
/// Implement this interface to subscribe to channel messages of type T.
/// </summary>
public interface ISubscribe<in T> where T : IPublish
{
    Task SubscribeAsync(T message, CancellationToken ct = default);
}
