using Highway.Server.Host.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Host;

/// <summary>
/// The broker as a hosted service (feature 031). Start builds and starts the server
/// through the public builder; stop disposes it — the same graceful teardown
/// <c>RunAsync</c> performs on cancellation: components first, then the recorder,
/// then Garnet commits and closes the AOF.
/// </summary>
internal sealed class HighwayBrokerService(HostConfiguration configuration, ILoggerFactory loggerFactory)
    : IHostedService
{
    private readonly ILogger<HighwayBrokerService> _logger = loggerFactory.CreateLogger<HighwayBrokerService>();
    private IHighwayServer? _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server = HighwayServerApplicator.BuildServer(configuration, loggerFactory);
        _server.Start();
        _logger.LogInformation("Highway broker listening on {Endpoint}", _server.Endpoint);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Highway broker stopping.");
        _server?.Dispose();
        _logger.LogInformation("Highway broker stopped.");
        return Task.CompletedTask;
    }
}
