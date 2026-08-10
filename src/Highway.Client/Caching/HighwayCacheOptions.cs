namespace Highway.Client.Caching;

/// <summary>
/// Configuration options for <see cref="HighwayCache"/>.
/// </summary>
public sealed class HighwayCacheOptions
{
    /// <summary>
    /// The Highway/Garnet server connection string. Required for standalone mode
    /// (<c>AddHighwayCache</c>); ignored when caching is added via <c>AddHighway</c>
    /// because the engine provides the connection.
    /// </summary>
    public string? Server { get; set; }

    /// <summary>
    /// Prefix prepended to every cache key before storage. Prevents collision with
    /// Highway's internal keys (<c>hw:svc:</c>, <c>hw:ch:</c>, etc.).
    /// </summary>
    public string KeyPrefix { get; set; } = "hw:cache:";
}
