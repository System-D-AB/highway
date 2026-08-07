using Highway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Highway.Client.Scanning;

/// <summary>
/// Registers discovered services and subscribers in the DI container with correct lifetimes.
/// </summary>
internal static class CatalogDiRegistrar
{
    public static void Register(IServiceCollection services, ScanResult scanResult)
    {
        foreach (var service in scanResult.Services)
        {
            services.Add(new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(
                service.ImplementationType,
                service.ImplementationType,
                MapLifetime(service.Lifetime)));
        }

        foreach (var channel in scanResult.Channels)
        {
            foreach (var subscriber in channel.Subscribers)
            {
                services.Add(new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(
                    subscriber.ImplementationType,
                    subscriber.ImplementationType,
                    MapLifetime(subscriber.Lifetime)));
            }
        }
    }

    private static ServiceLifetime MapLifetime(HighwayServiceLifetime lifetime) => lifetime switch
    {
        HighwayServiceLifetime.Singleton => ServiceLifetime.Singleton,
        HighwayServiceLifetime.Transient => ServiceLifetime.Transient,
        _ => ServiceLifetime.Scoped
    };
}
