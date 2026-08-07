namespace Highway.Client.Scanning;

/// <summary>
/// Immutable metadata about a discovered service.
/// </summary>
public sealed class ServiceDescriptor
{
    /// <summary>Service name from [Service] attribute.</summary>
    public required string Name { get; init; }

    /// <summary>The AsyncService&lt;T,TRes&gt; implementation type.</summary>
    public required Type ImplementationType { get; init; }

    /// <summary>The request type (T) implementing IReturn&lt;TRes&gt;.</summary>
    public required Type RequestType { get; init; }

    /// <summary>The response type (TRes) deriving from Output.</summary>
    public required Type ResponseType { get; init; }

    /// <summary>DI lifetime for this service.</summary>
    public required Highway.Abstractions.HighwayServiceLifetime Lifetime { get; init; }

    /// <summary>Pre-compiled delegate: (service, request, ct) → Task&lt;object&gt;</summary>
    public Func<object, object, CancellationToken, Task<object>>? InvokeDelegate { get; set; }

    /// <summary>Whether this is an internal Highway service (hidden from heartbeat).</summary>
    public bool IsInternal { get; init; }
}
