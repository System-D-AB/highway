namespace Highway.Abstractions;

/// <summary>
/// A message type implements <c>ISend</c> but carries no <c>[Queue]</c> attribute
/// (feature 014).
///
/// <para>The name is never inferred from the type name, because renaming the class would
/// then silently create a new queue and strand every message in the old one.</para>
/// </summary>
public sealed class QueueAttributeMissingException(Type messageType)
    : Exception($"The message type '{messageType.FullName}' implements ISend but has no [Queue(\"name\")] attribute. " +
                "A queue name is explicit so it survives renaming the class.");

/// <summary>
/// Two <c>IProcess&lt;T&gt;</c> implementations were found for the same message type
/// (feature 014).
///
/// <para>A queue has exactly one processor. Many instances of it compete for the work;
/// two different implementations would be fan-out, and fan-out is <c>PublishAsync</c>.</para>
/// </summary>
public sealed class DuplicateQueueProcessorException(Type messageType, Type first, Type second)
    : Exception($"Both '{first.FullName}' and '{second.FullName}' process '{messageType.FullName}'. " +
                "A queue has exactly one processor — use a channel and ISubscribe<T> if you want fan-out.");
