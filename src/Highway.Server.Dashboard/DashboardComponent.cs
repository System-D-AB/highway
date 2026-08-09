using System.Net;
using Highway.Server.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Dashboard;

/// <summary>
/// The dashboard component: hosts a minimal ASP.NET Core WebApplication
/// on its own port, serving the flight recorder UI.
/// </summary>
internal sealed class DashboardComponent : IHighwayServerComponent
{
    private readonly DashboardOptions _options;
    private readonly HighwayComponentContext _context;
    private WebApplication? _app;

    public DashboardComponent(DashboardOptions options, HighwayComponentContext context)
    {
        _options = options;
        _context = context;
    }

    public string Name => "Dashboard";

    public void Start()
    {
        var logger = _context.LoggerFactory.CreateLogger<DashboardComponent>();

        if (!_options.Enabled)
        {
            logger.LogDebug("Dashboard is disabled.");
            return;
        }

        try
        {
            var builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(_options.Bind, _options.Port);
            });

            builder.Logging.ClearProviders();
            // Use the same logger factory as the server
            builder.Services.AddSingleton(_context.LoggerFactory);

            // Register dependencies for endpoints
            builder.Services.AddSingleton(_options);
            builder.Services.AddSingleton(_context.Recorder);
            builder.Services.AddSingleton(new DashboardInfo(_context.Endpoint));
            builder.Services.AddSingleton(_context.BrokerState);
            builder.Services.AddSingleton(new StreamRegistry(_options.MaxConcurrentStreams));

            _app = builder.Build();

            if (!string.IsNullOrEmpty(_options.PathBase))
                _app.UsePathBase(_options.PathBase);

            // API key middleware
            if (_options.ApiKey is not null)
                _app.UseMiddleware<ApiKeyMiddleware>();

            // Map endpoints
            DashboardEndpoints.Map(_app);

            _app.StartAsync().GetAwaiter().GetResult();

            var url = $"http://{(_options.Bind.Equals(IPAddress.Loopback) ? "127.0.0.1" : _options.Bind)}:{_options.Port}{_options.PathBase}/";
            var keyNote = _options.ApiKey is not null ? " (API key required)" : "";

            if (!_options.Bind.Equals(IPAddress.Loopback) && _options.ApiKey is null)
            {
                logger.LogWarning(
                    "Highway dashboard listening on {Url} — bound beyond loopback WITHOUT an API key. " +
                    "Payload content may be served to any host on this network.",
                    url);
            }
            else
            {
                logger.LogInformation("Highway dashboard listening on {Url}{KeyNote}", url, keyNote);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Highway dashboard could not start on {Bind}:{Port} — {Reason}. " +
                "The broker is unaffected and continues to serve RESP on {BrokerEndpoint}.",
                _options.Bind, _options.Port, ex.Message, _context.Endpoint);
            _app = null;
        }
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            // Cancel all SSE streams before stopping the app
            _app.Services.GetService<StreamRegistry>()?.Dispose();

            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _app = null;
        }
    }
}

internal sealed record DashboardInfo(string BrokerEndpoint);
