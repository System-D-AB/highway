using Highway.Abstractions;
using Highway.Client;
using Highway.Samples;
using Highway.Samples.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// =============================================================================
// Highway.Samples.Storefront — calls services, and subscribes to events.
//
// Interactive rather than a fixed script, because the scenarios worth seeing
// (durable delivery across downtime, fan-out to two instances) need you to stop
// and start processes between commands.
//
//   dotnet run --project samples/Highway.Samples.Storefront
//   dotnet run --project samples/Highway.Samples.Storefront -- --node shop-2
// =============================================================================

var node   = SampleConfig.String(args, "--node", "HIGHWAY_NODE", "shop-1");
var server = SampleConfig.String(args, "--server", "HIGHWAY_SERVER", "127.0.0.1:6500");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Warning);   // quiet, so the demo output stands out

builder.Services.AddHighway(o =>
{
    o.NodeName = node;   // also the subscriber-group identity — unique per instance
    o.Server = server;

    // OFF by default in Highway, and this sample turns it on deliberately.
    //
    // Without it, calling a service that is a valid contract but that no node
    // currently hosts blocks for the full CallTimeout — 30 seconds of nothing.
    // With it, the client asks the registry first and returns 404 immediately.
    // The cost is one round trip on a cold cache, which is why it is opt-in.
    // Try `cancel ORD-1` with this set to false to feel the difference.
    o.FastFailEnabled = true;
});

var host = builder.Build();
var client = host.Services.GetRequiredService<IHighwayClient>();

Console.WriteLine($"""
    Highway storefront
      node   : {node}
      broker : {server}
    """);

try
{
    await host.StartAsync();
}
catch (Exception ex)
{
    // Scenario: broker not running. The failure should name the endpoint and be
    // actionable rather than a bare stack trace.
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nCould not start: {ex.Message}");
    Console.ResetColor();
    Console.WriteLine("Is the broker running?  dotnet run --project samples/Highway.Samples.Broker");
    return 1;
}

PrintHelp();

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null) break;                       // piped input exhausted

    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;

    var command = parts[0].ToLowerInvariant();
    if (command is "quit" or "exit") break;

    try
    {
        switch (command)
        {
            case "order":   await OrderAsync(parts); break;
            case "get":     await GetAsync(parts); break;
            case "cancel":  await CancelAsync(parts); break;
            case "low":     await LowAsync(parts); break;
            case "invoice": await InvoiceAsync(parts); break;
            case "poison":  await PoisonAsync(); break;
            case "dlq":     await DlqAsync(parts); break;
            case "discover": await DiscoverAsync(parts); break;
            case "stats":   await StatsAsync(parts); break;
            case "replay":  await ReplayAsync(parts); break;
            case "help":    PrintHelp(); break;
            default:
                Console.WriteLine($"  unknown command '{command}' — try 'help'");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
        Console.ResetColor();
    }
}

Console.WriteLine("Stopping...");
await host.StopAsync();
Console.WriteLine("Stopped.");
return 0;

// ---------------------------------------------------------------- commands

async Task OrderAsync(string[] parts)
{
    var quantity = parts.Length > 1 && int.TryParse(parts[1], out var q) ? q : 1;
    var item = parts.Length > 2 ? parts[2] : "widget";

    var response = await client.ExecuteAsync(new CreateOrder
    {
        CustomerId = 42,
        Item = item,
        Quantity = quantity,
    });

    if (response.StatusCode == StatusCodes.Status200OK)
        Console.WriteLine($"  {response.OrderId}  {quantity} x {item}  total {response.Total:C}");
    else
        PrintFailure(response);
}

async Task GetAsync(string[] parts)
{
    var id = parts.Length > 1 ? parts[1] : "ORD-0000";

    // The service returns 404 as DATA. Note there is no try/catch around this
    // call for the failure itself — ExecuteAsync does not throw for a
    // service-level outcome.
    var response = await client.ExecuteAsync(new GetOrder { OrderId = id });

    if (response.StatusCode == StatusCodes.Status200OK)
        Console.WriteLine($"  {response.OrderId}  total {response.Total:C}");
    else
        PrintFailure(response);
}

async Task CancelAsync(string[] parts)
{
    var id = parts.Length > 1 ? parts[1] : "ORD-0000";

    // orders.cancel is a real contract that nothing implements. The call is
    // therefore addressable but unroutable — the registry knows no live host.
    // With FastFailEnabled the client discovers that and returns 404 in
    // milliseconds; without it, this would block for the full 30s CallTimeout.
    var started = DateTime.UtcNow;
    var response = await client.ExecuteAsync(new CancelOrder { OrderId = id });
    var elapsed = DateTime.UtcNow - started;

    PrintFailure(response);
    Console.WriteLine($"  (failed in {elapsed.TotalMilliseconds:F0} ms — fast-fail via the registry, nothing enqueued)");
}

// ---------------------------------------------------------------------------
// Queue (feature 014). Contrast with `low`, which publishes to a channel: run two
// order services and `invoice` work is SHARED between them, while `low` is delivered
// to BOTH. Same topology, different verb, different behaviour.
// ---------------------------------------------------------------------------
async Task InvoiceAsync(string[] parts)
{
    var orderId = parts.Length > 1 ? parts[1] : "ORD-1";

    // SendAsync returns the message id — keep it, because it is how you find the
    // message in the dead-letter queue if the job misbehaves.
    var id = await client.SendAsync(new GenerateInvoice { OrderId = orderId, Total = 9.99m });

    Console.WriteLine($"  queued invoice for {orderId} (message {id})");
    Console.WriteLine("  exactly one processor will handle it — run two order services to see them share");
}

async Task PoisonAsync()
{
    var id = await client.SendAsync(new AlwaysFails { Reason = "demonstrating dead letters" });
    Console.WriteLine($"  queued a message that always fails (message {id})");
    Console.WriteLine("  it is retried up to MaxDeliveryAttempts, then dead-letters — try 'dlq poison.queue'");
}

async Task DlqAsync(string[] parts)
{
    var queue = parts.Length > 1 ? parts[1] : "poison.queue";

    // HW.DLQ is not on IHighwayClient — it is an operator command, so the sample
    // issues it over raw RESP exactly as a CLI or the dashboard would.
    await using var raw = await ConnectionMultiplexer.ConnectAsync(server);
    var result = (RedisResult[])(await raw.GetDatabase().ExecuteAsync("HW.DLQ", "PEEK", "Q", queue))!;

    if (result.Length == 0)
    {
        Console.WriteLine($"  no dead letters on '{queue}'");
        return;
    }

    Console.WriteLine($"  {result.Length} dead letter(s) on '{queue}':");
    foreach (var entry in result)
    {
        var fields = (RedisResult[])entry!;
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var name = fields[i].ToString();
            if (name is "payload") continue;   // the whole envelope; noisy here
            Console.WriteLine($"    {name,-16} {fields[i + 1]}");
        }
    }
}

async Task LowAsync(string[] parts)
{
    var item = parts.Length > 1 ? parts[1] : "widget";
    var remaining = parts.Length > 2 && int.TryParse(parts[2], out var r) ? r : 3;

    // A caller is also a publisher. The order service subscribes to this.
    await client.PublishAsync(new InventoryLow { Item = item, Remaining = remaining });
    Console.WriteLine($"  published InventoryLow: {item} ({remaining} remaining)");
}

// NOTE: discover and stats drop to raw RESP because Highway.Client exposes no
// public API for HW.DISCOVER / HW.STATS. See samples/RUNLOG.md, finding 2.
async Task  DiscoverAsync(string[] parts)
{
    var service = parts.Length > 1 ? parts[1] : "orders.create";

    using var redis = await ConnectionMultiplexer.ConnectAsync(server);
    var result = (RedisResult[])(await redis.GetDatabase().ExecuteAsync("HW.DISCOVER", service))!;

    if (result.Length == 0)
    {
        Console.WriteLine($"  no live node hosts '{service}'");
        return;
    }

    Console.WriteLine($"  '{service}' is hosted by:");
    foreach (var entry in result)
    {
        var pair = (RedisResult[])entry!;
        Console.WriteLine($"    {(string)pair[0]!}  (last beat {(string)pair[1]!}s ago)");
    }
}

// The flight recorder (feature 002): what the server has seen recently.
// Volatile by design — it is a debugging aid, not an audit log.
async Task ReplayAsync(string[] parts)
{
    var name = parts.Length > 1 ? parts[1] : "orders.create";

    using var redis = await ConnectionMultiplexer.ConnectAsync(server);
    var events = (RedisResult[])(await redis.GetDatabase()
        .ExecuteAsync("HW.REPLAY", name, "FROM", "-5min", "LIMIT", "20"))!;

    if (events.Length == 0)
    {
        Console.WriteLine($"  nothing recorded for '{name}' in the last 5 minutes");
        return;
    }

    Console.WriteLine($"  last {events.Length} operations on '{name}':");
    foreach (var raw in events)
    {
        var flat = (RedisResult[])raw!;
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            f[(string)flat[i]!] = (string)flat[i + 1]!;

        var when = DateTimeOffset.TryParse(f["timestamp"], out var ts) ? ts.ToString("HH:mm:ss.fff") : f["timestamp"];
        var detail = f["errorCode"].Length > 0 ? $"FAILED {f["errorCode"]}" : $"{f["payloadSize"]} bytes";
        Console.WriteLine($"    {when}  {f["eventType"],-20} {detail}");
    }
}

async Task StatsAsync(string[] parts)
{
    using var redis = await ConnectionMultiplexer.ConnectAsync(server);
    var db = redis.GetDatabase();

    var result = parts.Length > 1
        ? (RedisResult[])(await db.ExecuteAsync("HW.STATS", parts[1]))!
        : (RedisResult[])(await db.ExecuteAsync("HW.STATS"))!;

    for (var i = 0; i + 1 < result.Length; i += 2)
        Console.WriteLine($"    {(string)result[i]!,-12} {(string)result[i + 1]!}");
}

// ---------------------------------------------------------------- output

void PrintFailure(Output response)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  status {response.StatusCode}  {response.Error?.Code}: {response.Error?.Message}");
    Console.ResetColor();
}

void PrintHelp() => Console.WriteLine("""

      order <qty> [item]     place an order      (RPC, typed response)
      get <id>               fetch an order      (RPC, returns 404 as data)
      cancel <id>            call a service nobody hosts (fast-fail 404)
      low <item> [remaining] publish InventoryLow (every subscriber gets a copy)
      invoice [orderId]      queue work — exactly ONE processor handles it
      poison                 queue a message that always fails, to show dead-lettering
      dlq [queue]            inspect a queue's dead letters
      discover [service]     which nodes host a service
      stats [name]           server / service / channel counters
      stats recorder         flight recorder health
      replay [name]          recent recorded operations for a service
      help                   this list
      quit                   graceful shutdown
    """);
