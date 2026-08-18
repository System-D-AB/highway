namespace Highway.Client;

using StackExchange.Redis;

/// <summary>
/// Common connection settings contract implemented by <see cref="HighwayOptions"/> and <see cref="Caching.HighwayCacheOptions"/>.
/// </summary>
public interface IHighwayConnectionSettings
{
    /// <summary>
    /// Connection string for the Highway/Garnet server endpoint.
    /// </summary>
    string? Server { get; }

    /// <summary>
    /// Username for authentication.
    /// </summary>
    string? Username { get; }

    /// <summary>
    /// Password for authentication.
    /// </summary>
    string? Password { get; }

    /// <summary>
    /// Transport Layer Security (TLS) configuration.
    /// </summary>
    HighwayTlsOptions? Tls { get; }

    /// <summary>
    /// Delegate to customize the underlying StackExchange.Redis configuration before connecting.
    /// </summary>
    Action<ConfigurationOptions>? ConfigureConnection { get; }
}
