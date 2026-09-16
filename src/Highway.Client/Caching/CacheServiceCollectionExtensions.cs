using Highway.Client.Engine;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Highway.Client.Caching;

/// <summary>
/// DI wiring for the Highway broker-local cache (feature 044).
/// </summary>
public static class CacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HighwayCache"/> as the application's <see cref="IDistributedCache"/>,
    /// riding the same herd connection <c>AddHighway</c> established. Call after
    /// <c>AddHighway(...)</c>. Any <c>HybridCache</c> the app adds picks this up as its L2 with no
    /// further code (044 R3.2).
    ///
    /// <para>The cache is <b>broker-local and never replicated</b>: it is not durable across a
    /// failover (the herd's new master starts cold) and is wiped on any epoch change. It is a
    /// cache — a miss is a trip back to the system of record, not data loss.</para>
    /// </summary>
    public static IServiceCollection AddHighwayCache(
        this IServiceCollection services, Action<HighwayCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new HighwayCacheOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Uses the connection source AddHighway already registered — one multiplexer per process,
        // with the herd's failover behaviour, shared with the rest of the client.
        services.TryAddSingleton<IDistributedCache>(sp =>
            new HighwayCache(sp.GetRequiredService<HighwayConnectionSource>(), options));

        return services;
    }
}
