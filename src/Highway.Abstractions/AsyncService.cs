namespace Highway.Abstractions;

/// <summary>
/// Base class for service implementations. Derive from this to handle RPC requests.
/// </summary>
/// <typeparam name="TRequest">The request type (must implement IReturn&lt;TResponse&gt;).</typeparam>
/// <typeparam name="TResponse">The response type (must derive from Output).</typeparam>
public abstract class AsyncService<TRequest, TResponse>
    where TRequest : IReturn<TResponse>
    where TResponse : Output
{
    public abstract Task<TResponse> ExecuteAsync(TRequest request, CancellationToken ct = default);
}
