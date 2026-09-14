using Highway.Client.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// =============================================================================
// Highway.Samples.Host — demonstrates HighwayHost.RunAsync as a one-line host.
//
// This is the simplest possible app: a console app that installs itself as a
// Windows service or systemd unit, with no Highway shapes and no broker. It
// proves that Highway.Client.Hosting works standalone.
//
//   dotnet run --project samples/Highway.Samples.Host
//   dotnet run --project samples/Highway.Samples.Host -- install --name MyWorker
//   dotnet run --project samples/Highway.Samples.Host -- status  --name MyWorker
//   dotnet run --project samples/Highway.Samples.Host -- uninstall --name MyWorker
// =============================================================================

return await HighwayHost.RunAsync(
    args,
    highway: null,   // no Highway broker — this is a plain worker
    configure: builder =>
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddHostedService<TickWorker>();
    },
    hosting: new HostingOptions
    {
        ServiceName = "highway-sample-host",
        DisplayName = "Highway Sample Host",
        Description = "A sample worker that logs a tick every 5 seconds.",
        ShutdownTimeout = TimeSpan.FromSeconds(10),
    });

/// <summary>
/// A trivial background service that logs a tick every 5 seconds.
/// </summary>
internal sealed class TickWorker(ILogger<TickWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TickWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("tick — {Time:HH:mm:ss}", DateTime.Now);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("TickWorker stopped");
    }
}
