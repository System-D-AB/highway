using Highway.Abstractions;
using Highway.Samples.Contracts;

namespace Highway.Samples.Storefront;

/// <summary>
/// Receives every <see cref="OrderPlaced"/> the order service publishes.
///
/// <para>Nothing registers this class — assembly scanning finds it because it
/// implements <see cref="ISubscribe{T}"/>. Each running storefront gets its own
/// copy of every message, because a node's subscriber group is its node name.
/// Start two storefronts with different node names and both will print.</para>
///
/// <para>Writes straight to the console rather than the logger so that events
/// interleave visibly with the interactive prompt — the point of the demo is to
/// watch them arrive.</para>
/// </summary>
public sealed class OrderPlacedSubscriber : ISubscribe<OrderPlaced>
{
    public Task SubscribeAsync(OrderPlaced message, CancellationToken ct = default)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  << event: OrderPlaced {message.OrderId} — {message.Item} {message.Total:C}");
        Console.ForegroundColor = previous;
        Console.Write("> ");
        return Task.CompletedTask;
    }
}
