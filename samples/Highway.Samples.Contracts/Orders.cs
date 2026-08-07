using Highway.Abstractions;

namespace Highway.Samples.Contracts;

// =============================================================================
// Why this project exists
//
// Two processes exchanging typed messages must agree on the types. If each app
// defined its own CreateOrder, the sample would demonstrate copy-paste
// duplication and quietly teach the wrong pattern.
//
// This library references ONLY Highway.Abstractions — a small package with no
// transitive dependencies. The caller and the service host both reference it,
// and neither takes a dependency on the other or on the client engine. That is
// precisely what the three-package split buys, and it is why the split exists:
// see docs/product/product.md § "Delivery (Package Architecture)".
//
// Contracts only. No service implementations, no subscribers, no hosting code.
// =============================================================================

/// <summary>Place an order. Handled by Highway.Samples.OrderService.</summary>
[Service("orders.create")]
public sealed class CreateOrder : IReturn<OrderResult>
{
    public int CustomerId { get; set; }
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>
/// Response for both order services. Extends <see cref="Output"/>, which
/// carries <c>StatusCode</c> and <c>Error</c> — this is how Highway returns
/// failures as data rather than as exceptions.
/// </summary>
public sealed class OrderResult : Output
{
    public string? OrderId { get; set; }
    public decimal Total { get; set; }
}

/// <summary>
/// Fetch an order. The sample implementation always returns 404 as data, to
/// demonstrate that a service-level failure reaches the caller as a status code
/// with no exception thrown anywhere.
/// </summary>
[Service("orders.get")]
public sealed class GetOrder : IReturn<OrderResult>
{
    public string OrderId { get; set; } = "";
}

/// <summary>
/// A contract with <b>no implementation anywhere</b>, on purpose.
///
/// <para>Calling it exercises the local-catalog 404: the caller's own catalog
/// has no service registered for this request type, so <c>ExecuteAsync</c>
/// fails immediately without a network round trip — a different and much better
/// failure than waiting out the call timeout.</para>
/// </summary>
[Service("orders.cancel")]
public sealed class CancelOrder : IReturn<OrderResult>
{
    public string OrderId { get; set; } = "";
}
