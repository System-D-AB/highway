using Highway.Client.Engine;
using Microsoft.Extensions.Hosting;

namespace Highway.Client.Hosting;

/// <summary>
/// Bridges the Highway engine into the .NET Generic Host lifecycle: the engine
/// starts with the host and drains/stops with it. In non-hosted applications,
/// resolve <see cref="IHighwayEngine"/> and call StartAsync/StopAsync manually.
/// </summary>
public sealed class HighwayEngineHostedService(IHighwayEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => engine.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => engine.StopAsync(cancellationToken);
}
