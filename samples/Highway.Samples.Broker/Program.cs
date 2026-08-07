using System.Net;
using Highway.Samples;
using Highway.Server;
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

var server = new HighwayServerBuilder()
    .WithPort(port)
    .WithBindAddress(bind)
    .WithDataDir(dataDir)          // durability on by default, so restarts are observable
    .WithLoggerFactory(loggerFactory)
    .Build();

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
