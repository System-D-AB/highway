using Highway.Abstractions;

namespace Highway.Samples.Contracts;

/// <summary>
/// Published by the order service after it creates an order.
///
/// <para>Every subscribing node gets its own copy, and a node that is offline
/// when this is published receives it when it restarts under the same node
/// name — Highway's pub/sub is durable, not fire-and-forget.</para>
/// </summary>
[Channel("orders.placed")]
public sealed class OrderPlaced : IPublish
{
    public string OrderId { get; set; } = "";
    public string Item { get; set; } = "";
    public decimal Total { get; set; }
}

/// <summary>
/// Published by the storefront. Demonstrates that a caller is also a publisher,
/// and that the service host is also a subscriber — there is no rigid
/// client/server split in Highway.
/// </summary>
[Channel("inventory.low")]
public sealed class InventoryLow : IPublish
{
    public string Item { get; set; } = "";
    public int Remaining { get; set; }
}
