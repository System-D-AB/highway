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
}
