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

    /// <summary>
    /// How long a caller waits for this service, as a <c>TimeSpan</c> string (<c>"00:05:00"</c>) — for a service whose
    /// work legitimately takes longer than <c>CallTimeout</c> (074). The broker stops running a request when its
    /// caller's time is up, and clamps any timeout to its <c>MaxRpcTimeout</c> (1 hour by default). A per-call
    /// timeout (<c>ExecuteAsync(request, timeout)</c>) overrides it. Invalid text is a startup error.
    /// </summary>
    public string? Timeout { get; init; }
}
