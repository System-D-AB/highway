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
    /// A failing subscriber does not abort siblings.
    /// </summary>
    public async Task<ChannelResponse> ExecuteSubscribersAsync(string channelName, object message, CancellationToken ct = default)
    {
        var descriptor = _catalog.GetChannelDescriptor(channelName);
        if (descriptor is null)
        {
            return new ChannelResponse { TotalSubscribers = 0, SuccessCalls = 0 };
        }

        var total = descriptor.Subscribers.Count;
        var success = 0;

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
            catch
            {
                // Subscriber exceptions are swallowed — one failure doesn't abort siblings
            }
        }

        return new ChannelResponse { TotalSubscribers = total, SuccessCalls = success };
    }
}
