using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Execution;
using Highway.Client.Hosting;
using Highway.Client.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Highway.Client;

/// <summary>
/// Extension methods for registering Highway in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Scans assemblies for Highway services and channels, builds the catalog,
    /// and registers all infrastructure in the DI container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action. Server is required.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHighway(this IServiceCollection services, Action<HighwayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HighwayOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.Server))
            throw new InvalidOperationException(
                "HighwayOptions.Server is required. Highway always communicates through a server. " +
                "Use HighwayTestServer for integration tests.");

        // Fail fast on every other misconfiguration (feature 005, Requirement 12).
        HighwayOptionsValidator.Validate(options);

        // 1. Discover assemblies
        var assemblySource = new DefaultAssemblySource(options);
        var assemblies = assemblySource.GetAssemblies();

        // 2. Scan for services and channels
        var typeScanner = new DefaultTypeScanner();
        var scanResult = typeScanner.Scan(assemblies);

        // 3. Compile dispatch delegates
        var delegateCompiler = new ExpressionDelegateCompiler();
        foreach (var service in scanResult.Services)
        {
            service.InvokeDelegate = delegateCompiler.CompileServiceDelegate(
                service.ImplementationType, service.RequestType, service.ResponseType);
        }
        foreach (var channel in scanResult.Channels)
        {
            foreach (var subscriber in channel.Subscribers)
            {
                subscriber.InvokeDelegate = delegateCompiler.CompileSubscriberDelegate(
                    subscriber.ImplementationType, channel.MessageType);
            }
        }

        foreach (var queue in scanResult.Queues)
        {
            queue.InvokeDelegate = delegateCompiler.CompileProcessorDelegate(
                queue.ProcessorType, queue.MessageType);
        }

        // 4. Build immutable catalog
        var catalog = new ImmutableCatalog(
            scanResult.Services,
            scanResult.Channels,
            scanResult.RequestContracts,
            scanResult.MessageContracts,
            scanResult.Queues,
            scanResult.QueueContracts);

        // 5. Register discovered types in DI
        CatalogDiRegistrar.Register(services, scanResult);

        // 6. Register Highway infrastructure
        services.AddSingleton(options);
        services.AddSingleton<ICatalog>(catalog);
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.TryAddSingleton<ServiceExecutor>();

        // 7. Register the engine (feature 005): one instance behind both the
        // public lifecycle interface and the internal wiring interface, plus
        // the hosted-service bridge for the .NET Generic Host.
        services.TryAddSingleton<HighwayEngine>();
        services.TryAddSingleton<IHighwayEngine>(sp => sp.GetRequiredService<HighwayEngine>());
        services.TryAddSingleton<IHighwayEngineInternals>(sp => sp.GetRequiredService<HighwayEngine>());
        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IHostedService, HighwayEngineHostedService>());

        services.TryAddSingleton<IHighwayClient, HighwayClient>();

        return services;
    }
}
