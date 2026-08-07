using Highway.Abstractions;
using Highway.Samples.Contracts;
using Microsoft.Extensions.Logging;

namespace Highway.Samples.OrderService;

/// <summary>
/// The success path: takes a request, returns typed data, and publishes an
/// event. Nothing registers this class — assembly scanning finds it because it
/// extends <see cref="AsyncService{TRequest,TResponse}"/>.
/// </summary>
public sealed class CreateOrderService(
    ILogger<CreateOrderService> log,
    IHighwayClient client)
    : AsyncService<CreateOrder, OrderResult>
{
    public override async Task<OrderResult> ExecuteAsync(CreateOrder request, CancellationToken ct = default)
    {
        var orderId = $"ORD-{Random.Shared.Next(1000, 9999)}";
        var total = request.Quantity * 9.99m;

        log.LogInformation("Created {OrderId}: {Quantity} x {Item} for customer {CustomerId} = {Total:C}",
            orderId, request.Quantity, request.Item, request.CustomerId, total);

        // A node that serves is also a node that publishes. IHighwayClient is
        // injected into the service exactly as it would be into a controller.
        await client.PublishAsync(
            new OrderPlaced { OrderId = orderId, Item = request.Item, Total = total }, ct);

        return new OrderResult
        {
            OrderId = orderId,
            Total = total,
            StatusCode = StatusCodes.Status200OK,
        };
    }
}

/// <summary>
/// The error path. Note what is <b>not</b> here: no thrown exception, no
/// try/catch at the call site, no error-handling middleware. A failure is a
/// status code and an <see cref="ErrorDetail"/> on the response — the caller
/// reads it like any other field.
/// </summary>
public sealed class GetOrderService(ILogger<GetOrderService> log)
    : AsyncService<GetOrder, OrderResult>
{
    public override Task<OrderResult> ExecuteAsync(GetOrder request, CancellationToken ct = default)
    {
        log.LogInformation("Lookup for {OrderId} — this sample stores nothing, so it always 404s",
            request.OrderId);

        return Task.FromResult(new OrderResult
        {
            StatusCode = StatusCodes.Status404NotFound,
            Error = new ErrorDetail
            {
                Code = "ORDER_NOT_FOUND",
                Message = $"No order with id '{request.OrderId}'.",
            },
        });
    }
}

/// <summary>
/// The service host is also a subscriber. Highway has no rigid client/server
/// split: any node can serve, call, publish and subscribe.
/// </summary>
public sealed class InventoryLowSubscriber(ILogger<InventoryLowSubscriber> log)
    : ISubscribe<InventoryLow>
{
    public Task SubscribeAsync(InventoryLow message, CancellationToken ct = default)
    {
        log.LogWarning("Inventory low: {Item} ({Remaining} remaining)", message.Item, message.Remaining);
        return Task.CompletedTask;
    }
}
