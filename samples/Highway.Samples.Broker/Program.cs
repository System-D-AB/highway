using System.Net;
using Highway.Samples;
using Highway.Server;
using Highway.Server.Dashboard;
using Microsoft.Extensions.Logging;

// =============================================================================
// Highway.Samples.Broker — the Highway server, as a standalone process.
//
// This is the same public API a production deployment would use:
// HighwayServerBuilder → Build() → RunAsync(CancellationToken). No test hooks,
// no internal access, no sample-only shortcuts.
//
//   dotnet run --project samples/Highway.Samples.Broker
// =============================================================================

var port    = SampleConfig.Int(args, "--port", "HIGHWAY_PORT", 6500);
var dataDir = SampleConfig.String(args, "--data-dir", "HIGHWAY_DATA_DIR", "./data");
var bind    = SampleConfig.Address(args, "--bind", "HIGHWAY_BIND", IPAddress.Loopback);

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

// ── Optional: collect Highway's traces with OpenTelemetry ────────────────────
//
// Highway emits System.Diagnostics.Activity and takes NO OpenTelemetry
// dependency, so the application chooses its own pipeline. To export traces,
// add the OpenTelemetry packages and subscribe to Highway's two sources:
//
//   dotnet add package OpenTelemetry.Extensions.Hosting
//   dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
//
//   using var tracing = Sdk.CreateTracerProviderBuilder()
//       .AddSource("Highway.Server")     // server-side command spans
//       .AddSource("Highway.Client")     // caller-side call/publish spans
//       .ConfigureResource(r => r.AddService("highway-broker"))
//       .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"))
//       .Build();
//
// Without this, spans are emitted and nothing collects them, which costs
// essentially nothing — StartActivity returns null when no listener exists.

var dashboardPort = SampleConfig.Int(args, "--dashboard-port", "HIGHWAY_DASHBOARD_PORT", 7500);

// Lease and attempt limit are exposed so the samples can demonstrate dead-lettering in a
// session rather than in half an hour: the defaults (5 minutes x 5 attempts) are right for
// production and mean a poison message takes ~25 minutes to reach the DLQ.
//
//   --lease-seconds 2 --max-attempts 2   → a poison message dead-letters in about 6 seconds
var leaseSeconds = SampleConfig.Int(args, "--lease-seconds", "HIGHWAY_LEASE_SECONDS", 0);
var maxAttempts  = SampleConfig.Int(args, "--max-attempts", "HIGHWAY_MAX_ATTEMPTS", 0);

// Authentication is off by default because that is the evaluation path: a loopback broker
// with no password is the right configuration for trying Highway. Set HIGHWAY_PASSWORD (or
// --password) to secure it, and every participant must then present the same value.
var password = SampleConfig.String(args, "--password", "HIGHWAY_PASSWORD", "");

var builder = new HighwayServerBuilder()
    .WithPort(port)
    .WithBindAddress(bind)
    .WithDataDir(dataDir)          // durability on by default, so restarts are observable
    // The dashboard binds loopback and serves recorded message payloads, so it is
    // safe here and would need an API key anywhere else (feature 011).
    .WithDashboard(dashboardPort)
    .WithOptions(o =>
    {
        if (leaseSeconds > 0) o.Lease = TimeSpan.FromSeconds(leaseSeconds);
        if (maxAttempts > 0) o.MaxDeliveryAttempts = maxAttempts;
    })
    .WithLoggerFactory(loggerFactory);

if (!string.IsNullOrEmpty(password))
    builder.WithPassword(password);

var server = builder.Build();

Console.WriteLine($"Dashboard: http://127.0.0.1:{dashboardPort}/");

// Ctrl+C must drain rather than kill: e.Cancel = true stops the runtime from
// terminating the process, and the token lets RunAsync shut down cleanly.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Shutting down (Ctrl+C)...");
    cts.Cancel();
};

Console.WriteLine($"""
    Highway broker
      listening on : {bind}:{port}
      data dir     : {Path.GetFullPath(dataDir)}

    Connect participants with:  --server {bind}:{port}
    Press Ctrl+C to stop.
    """);

await server.RunAsync(cts.Token);

Console.WriteLine("Broker stopped.");
return 0;
