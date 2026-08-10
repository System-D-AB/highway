using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Highway.Client.Caching;

/// <summary>
/// Extension methods for registering Highway's distributed cache in standalone mode
/// (no messaging engine, no worker loops, no heartbeat).
/// </summary>
public static class CacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HighwayCache"/> as <see cref="IDistributedCache"/> with its own
    /// <see cref="ConnectionMultiplexer"/>. This entry point is for applications that want
    /// only the cache — no Highway engine, no assembly scanning, no worker loops.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Configuration action. <see cref="HighwayCacheOptions.Server"/> is required; an
    /// <see cref="InvalidOperationException"/> is thrown immediately if it is not set.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="HighwayCacheOptions.Server"/> is null or whitespace.
    /// </exception>
    public static IServiceCollection AddHighwayCache(
        this IServiceCollection services, Action<HighwayCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HighwayCacheOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.Server))
            throw new InvalidOperationException(
                "HighwayCacheOptions.Server is required. " +
                "Provide the Highway/Garnet server connection string.");

        // Create a dedicated connection for cache-only mode.
        var mux = ConnectionMultiplexer.Connect(options.Server);
        var cache = new HighwayCache(mux, options);

        services.TryAddSingleton<IDistributedCache>(cache);

        return services;
    }
}
