namespace Highway.Abstractions;

/// <summary>
/// Marks a request type as an RPC service with the given name.
/// Applied to the request class, not the service implementation.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ServiceAttribute(string name) : Attribute
{
    /// <summary>
    /// The unique service name used for routing (e.g., "orders.create").
    /// </summary>
    public string Name { get; } = name;
}
