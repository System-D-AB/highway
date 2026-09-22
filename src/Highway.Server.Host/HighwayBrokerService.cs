using Highway.Server.Host.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Host;

/// <summary>
/// The broker as a hosted service (feature 031). Start builds and starts the server
/// through the public builder; stop disposes it — the same graceful teardown
/// <c>RunAsync</c> performs on cancellation: components first, then the recorder,
/// then the store is flushed and closed.
/// </summary>
internal sealed class HighwayBrokerService(
    HostConfiguration configuration,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime)
    : IHostedService
{
    private readonly ILogger<HighwayBrokerService> _logger = loggerFactory.CreateLogger<HighwayBrokerService>();
    private IHighwayServer? _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server = HighwayServerApplicator.BuildServer(configuration, loggerFactory);

        // 050 T3: when replication schedules an auto-rejoin, a demoted ex-primary must restart so the
        // store re-syncs it as the new primary's replica. Stop the application; the service supervisor
        // (Windows service recovery / systemd Restart= / the run loop) relaunches it, and Open honours
        // the rejoin marker. Subscribed before Start so no signal is missed.
        _server.RejoinRequested += () =>
        {
            _logger.LogWarning(
                "Highway broker: replication scheduled an auto-rejoin — stopping so the service restarts and re-syncs as a replica of the new primary.");
            lifetime.StopApplication();
        };

        // Pass the start token so a blank replica waiting for its primary (058 R3) can be shut down
        // cleanly mid-wait; the RESP host's overload honours it, the interface default does not.
        if (_server is RespHighwayServer resp)
            resp.Start(cancellationToken);
        else
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
