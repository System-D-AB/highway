using Highway.Abstractions;
using Highway.Client.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace Highway.Client.Execution;

/// <summary>
/// Executes services and subscribers locally when work arrives from the server.
/// Creates a DI scope per invocation for proper lifetime management.
/// </summary>
public sealed class ServiceExecutor
{
    private readonly ICatalog _catalog;
    private readonly IServiceScopeFactory _scopeFactory;

    public ServiceExecutor(ICatalog catalog, IServiceScopeFactory scopeFactory)
    {
        _catalog = catalog;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Runs a queue processor (feature 014).
    ///
    /// <para>No response object: the sender is not waiting. A handler that throws
    /// propagates, so the message is never acknowledged and lease recovery redelivers it —
    /// which is the whole point of at-least-once. Swallowing the exception here would
    /// silently discard work.</para>
    /// </summary>
    public async Task ExecuteProcessorAsync(string queueName, object message, CancellationToken ct = default)
    {
        var descriptor = _catalog.GetQueue(queueName)
            ?? throw new InvalidOperationException($"No processor registered for queue '{queueName}'.");

        if (descriptor.InvokeDelegate is null)
            throw new InvalidOperationException($"Queue '{queueName}' has no compiled delegate.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService(descriptor.ProcessorType);

        await descriptor.InvokeDelegate(processor, message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a service by name. Creates a scope, resolves the service, and invokes it.
    /// </summary>
    /// <returns>The response object, or a GenericOutput with error status.</returns>
    public async Task<object> ExecuteServiceAsync(string serviceName, object request, CancellationToken ct = default)
    {
        var descriptor = _catalog.GetServiceDescriptor(serviceName);
        if (descriptor is null)
        {
            return new GenericOutput
            {
                StatusCode = StatusCodes.Status404NotFound,
                Error = new ErrorDetail
                {
                    Code = "SERVICE_NOT_FOUND",
                    Message = $"No service registered with name '{serviceName}'."
                }
            };
        }

        if (descriptor.InvokeDelegate is null)
        {
            return new GenericOutput
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                Error = new ErrorDetail
                {
                    Code = "NO_DELEGATE",
                    Message = $"Service '{serviceName}' has no compiled delegate."
                }
            };
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceInstance = scope.ServiceProvider.GetRequiredService(descriptor.ImplementationType);

        try
        {
            var result = await descriptor.InvokeDelegate(serviceInstance, request, ct).ConfigureAwait(false);
            if (result is Output output)
            {
                output.StatusCode ??= StatusCodes.Status200OK;
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new GenericOutput
            {
                StatusCode = StatusCodes.Status408RequestTimeout,
                Error = new ErrorDetail
                {
                    Code = "CANCELLED",
                    Message = "Request was cancelled."
                }
            };
        }
        catch (Exception ex)
        {
            return new GenericOutput
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                Error = new ErrorDetail
                {
                    Code = "SERVICE_EXCEPTION",
                    Message = ex.Message,
                    StackTrace = ex.StackTrace
                }
            };
        }
    }

    /// <summary>
    /// Executes all subscribers for a channel. Each subscriber runs in its own scope.
    ///
    /// <para><b>Attempt all, then fail (018 T2a).</b> A failing subscriber does not abort its
    /// siblings — every handler gets its attempt — but if any of them threw, this method
    /// <b>throws</b> rather than reporting a count nobody reads. That is what makes a failed
    /// pub/sub delivery reach the dead letter with 015's context instead of being acknowledged
    /// and lost.</para>
    ///
    /// <para><b>Why this changed.</b> Until 018 the exceptions were swallowed here and the
    /// message was acknowledged regardless, so a subscriber that failed every time was
    /// invisible: no redelivery, no dead letter, nothing in any log but the application's own.
    /// The publisher can do nothing about a subscriber's failure — they are different processes
    /// in different places — which is exactly why the <i>subscriber's</i> side has to keep the
    /// evidence. Its group has its own dead-letter list; the publisher is never involved.</para>
    ///
    /// <para><b>The cost, stated:</b> a redelivery re-runs the siblings that already succeeded.
    /// At-least-once delivery already requires idempotent handlers, and <c>[Idempotent]</c> —
    /// which became functional for subscribers in the same change — is the remedy for handlers
    /// that cannot be.</para>
    /// </summary>
    /// <exception cref="AggregateException">One or more subscribers threw.</exception>
    public async Task<ChannelResponse> ExecuteSubscribersAsync(string channelName, object message, CancellationToken ct = default)
    {
        var descriptor = _catalog.GetChannelDescriptor(channelName);
        if (descriptor is null)
        {
            return new ChannelResponse { TotalSubscribers = 0, SuccessCalls = 0 };
        }

        var total = descriptor.Subscribers.Count;
        var success = 0;
        List<Exception>? failures = null;

        foreach (var subscriber in descriptor.Subscribers)
        {
            if (subscriber.InvokeDelegate is null) continue;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var instance = scope.ServiceProvider.GetRequiredService(subscriber.ImplementationType);
                await subscriber.InvokeDelegate(instance, message, ct).ConfigureAwait(false);
                success++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not failure. The message is simply not acknowledged and the lease
                // sweep redelivers it — consuming an attempt here would punish a clean stop.
                throw;
            }
            catch (Exception ex)
            {
                // Collected, not rethrown yet: the remaining subscribers still get their turn.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            // A single failure is surfaced as itself. Wrapping one exception in an aggregate
            // buries the type and message that the dead letter exists to show.
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    $"{failures.Count} of {total} subscribers for '{channelName}' failed.", failures);
        }

        return new ChannelResponse { TotalSubscribers = total, SuccessCalls = success };
    }
}
