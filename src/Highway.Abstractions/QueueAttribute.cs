namespace Highway.Abstractions;

/// <summary>
/// Names the queue a message type is sent to (feature 014).
///
/// <para><b>The name is explicit and is never inferred from the type name.</b> A convention
/// would be shorter to type and is a data-loss refactor waiting to happen: renaming the
/// class would silently create a new queue while every message already in the old one is
/// stranded with no processor and no error. A queue is a durable store, so its address must
/// survive refactoring — which means it must be written down.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class QueueAttribute(string name) : Attribute
{
    /// <summary>The queue name. Subject to the same identifier rules as every Highway name.</summary>
    public string Name { get; } = name;
}
