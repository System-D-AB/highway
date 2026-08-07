using Highway.Abstractions;

namespace Highway.Client.Scanning;

/// <summary>
/// Immutable metadata about a discovered queue (feature 014).
///
/// <para>Unlike a channel, which may have many subscribers, a queue has <b>exactly one</b>
/// processor type. Many <i>instances</i> of it compete; two different implementations is an
/// error, because that would be fan-out and fan-out is what <c>PublishAsync</c> is for.</para>
/// </summary>
public sealed class QueueDescriptor
{
    /// <summary>Queue name from the <c>[Queue]</c> attribute.</summary>
    public required string Name { get; init; }

    /// <summary>The message type implementing <c>ISend</c>.</summary>
    public required Type MessageType { get; init; }

    /// <summary>The single <c>IProcess&lt;T&gt;</c> implementation.</summary>
    public required Type ProcessorType { get; init; }

    /// <summary>DI lifetime for the processor.</summary>
    public HighwayServiceLifetime Lifetime { get; init; } = HighwayServiceLifetime.Scoped;

    /// <summary>Pre-compiled delegate: (processor, message, ct) → Task.</summary>
    public Func<object, object, CancellationToken, Task>? InvokeDelegate { get; set; }
}
