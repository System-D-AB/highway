namespace Highway.Abstractions;

/// <summary>
/// Marks a message type as a Pub/Sub channel message with the given channel name.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ChannelAttribute(string name) : Attribute
{
    /// <summary>
    /// The channel name used for pub/sub routing.
    /// </summary>
    public string Name { get; } = name;
}
