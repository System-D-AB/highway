namespace Highway.Client.Scanning;

/// <summary>
/// Metadata about a single subscriber for a channel.
/// </summary>
public sealed class SubscriberDescriptor
{
    /// <summary>The ISubscribe&lt;T&gt; implementation type.</summary>
    public required Type ImplementationType { get; init; }

    /// <summary>DI lifetime for this subscriber.</summary>
    public required Highway.Abstractions.HighwayServiceLifetime Lifetime { get; init; }

    /// <summary>Pre-compiled delegate: (subscriber, message, ct) → Task</summary>
    public Func<object, object, CancellationToken, Task>? InvokeDelegate { get; set; }
}
