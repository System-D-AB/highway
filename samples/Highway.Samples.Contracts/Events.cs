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

// ---------------------------------------------------------------------------
// Queue (feature 014) — work handled by exactly ONE processor.
//
// Contrast with InventoryLow above, which is a [Channel]: every subscriber node
// receives its own copy. This is the whole distinction between the two verbs:
//
//   three instances of a processor  → they SHARE the work
//   three instances of a subscriber → each gets its OWN copy
//
// One handler → SendAsync. Many handlers → PublishAsync. Need the answer → ExecuteAsync.
// ---------------------------------------------------------------------------

/// <summary>
/// Generate an invoice for an order. Fire-and-forget work: the storefront does not
/// wait for it, and exactly one worker performs it.
/// </summary>
[Queue("invoices.generate")]
public sealed record GenerateInvoice : ISend
{
    public string OrderId { get; init; } = "";
    public decimal Total { get; init; }
}

/// <summary>
/// Deliberately fails every time, to demonstrate dead-lettering (feature 013).
/// After MaxDeliveryAttempts the message leaves the queue and lands in the DLQ, where
/// <c>HW.DLQ PEEK Q poison.queue</c> can find it — instead of looping forever.
/// </summary>
[Queue("poison.queue")]
public sealed record AlwaysFails : ISend
{
    public string Reason { get; init; } = "";
}
