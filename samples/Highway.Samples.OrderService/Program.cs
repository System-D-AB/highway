using Highway.Client;
using Highway.Samples.Contracts;
using Highway.Samples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// =============================================================================
// Highway.Samples.OrderService — hosts services and publishes events.
//
// Standard .NET generic host. AddHighway scans, registers everything it finds,
// and adds the hosted service that starts and drains the engine with the host.
// There is no manual StartAsync, and no service or subscriber is registered by
// hand anywhere in this app.
//
//   dotnet run --project samples/Highway.Samples.OrderService
//   dotnet run --project samples/Highway.Samples.OrderService -- --node order-service-2
// =============================================================================

var node   = SampleConfig.String(args, "--node", "HIGHWAY_NODE", "order-service-1");
var server = SampleConfig.String(args, "--server", "HIGHWAY_SERVER", "127.0.0.1:6500");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddHighway(o =>
{
    o.NodeName = node;   // unique per running instance, always
    o.Server = server;

    // 025: all order-service instances are replicas of ONE logical subscriber.
    // They compete for each InventoryLow event instead of each receiving a copy —
    // run two instances, publish 'low widget', and exactly one of them reacts.
    // Delete this line and each instance becomes its own group: every instance
    // then gets every event, which is right for things like cache invalidation.
    o.SubscriptionGroup = "order-service";

    // 028: a recurring job. The broker fires ONE ReconcileOrders per minute, whichever
    // and however many instances are running; processing competes like any queue work.
    o.Jobs.Every<ReconcileOrders>(TimeSpan.FromMinutes(1));
});

Console.WriteLine($"""
    Highway order service
      node   : {node}
      broker : {server}

    Hosting : orders.create, orders.get
    Watching: inventory.low
    Press Ctrl+C to stop.
    """);

await builder.Build().RunAsync();
