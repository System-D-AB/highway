namespace Highway.Client.Caching;

using StackExchange.Redis;

/// <summary>
/// Configuration options for <see cref="HighwayCache"/>.
/// </summary>
public sealed class HighwayCacheOptions : IHighwayConnectionSettings
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

    /// <summary>
    /// Username for ACL-based authentication.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for authentication.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Transport Layer Security (TLS) configuration.
    /// </summary>
    public HighwayTlsOptions? Tls { get; set; }

    /// <summary>
    /// Hook to customize the underlying StackExchange.Redis configuration before connecting.
    /// </summary>
    public Action<ConfigurationOptions>? ConfigureConnection { get; set; }
}
