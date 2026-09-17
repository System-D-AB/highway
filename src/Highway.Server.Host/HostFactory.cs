using Highway.Server.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Highway.Server.Host;

/// <summary>
/// Builds the Generic Host for a loaded configuration (feature 031, design § Host
/// Lifecycle). Extracted from <c>Program</c> so tests can drive the full host —
/// <c>StartAsync</c>/<c>StopAsync</c> — without spawning a process.
/// </summary>
internal static class HostFactory
{
    /// <summary>
    /// How long the host waits for the broker's graceful stop before its shutdown
    /// timeout cancels the stop token. The host still awaits the stop itself (the
    /// timeout cancels a token, it does not abort the await — feature 021's
    /// insight, applied to the broker); the window keeps service managers honest.
    /// </summary>
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    public static HostApplicationBuilder Create(HostConfiguration configuration)
    {
        // Fully-qualified: 'Host' alone resolves to the Highway.Server.Host namespace here.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);

        // Feature 045: replace the default logging pipeline with Serilog (console + rolling files
        // in the distribution's logs/ folder). A broker installed as a Windows service has no
        // console for the default provider to reach, so without a file sink it produced no
        // readable log at all. ClearProviders drops the default Console/EventLog set so there is
        // exactly one pipeline.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(
            HostLogging.CreateLogger(HostLogging.ResolveLogDirectory()), dispose: true);

        // The mode is detected, never declared: both are registered and each no-ops
        // off its platform, so exactly one takes effect and the other costs nothing.
        builder.Services.AddWindowsService();
        builder.Services.AddSystemd();

        builder.Services.AddHostedService(sp =>
            new HighwayBrokerService(
                configuration,
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IHostApplicationLifetime>()));

        return builder;
    }
}
