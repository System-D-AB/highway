using Highway.Abstractions;

namespace Highway.Client.Scanning;

/// <summary>
/// Immutable metadata about a discovered dead-letter handler (feature 073).
/// </summary>
public sealed class DeadLetterDescriptor
{
    /// <summary>The target kind (Queue, Service, or Group).</summary>
    public required DeadLetterTargetKind TargetKind { get; init; }

    /// <summary>The target entity name (queue name, service name, or channel name).</summary>
    public required string TargetName { get; init; }

    /// <summary>The contract type handled by the dead-letter handler.</summary>
    public required Type MessageType { get; init; }

    /// <summary>The concrete handler type implementing <see cref="IDeadLetter{T}"/>.</summary>
    public required Type HandlerType { get; init; }

    /// <summary>DI lifetime for the handler.</summary>
    public HighwayServiceLifetime Lifetime { get; init; } = HighwayServiceLifetime.Scoped;

    /// <summary>Pre-compiled delegate: (handler, deadLetterObject, ct) → Task&lt;DeadLetterAction&gt;.</summary>
    public Func<object, object, CancellationToken, Task<DeadLetterAction>>? InvokeDelegate { get; set; }
}
