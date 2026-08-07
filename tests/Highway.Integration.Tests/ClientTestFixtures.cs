using System.Collections.Concurrent;
using Highway.Abstractions;
using Highway.Client;
using Highway.Client.Engine;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Highway.Integration.Tests;

// =============================================================================
// Feature 005 integration fixtures.
//
// Every engine scans THIS assembly, so every node hosts every service and
// channel defined here. Tests are written with that topology in mind:
//   - RPC correctness is asserted via caller-side correlation
//   - pub/sub fan-out is observed through the static recorder
// =============================================================================

/// <summary>
/// One running Highway engine + client, built through the real AddHighway
/// pipeline (non-hosted path: IHighwayEngine.StartAsync directly).
/// </summary>
internal sealed class EngineNode : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public IHighwayClient Client { get; }
    public IHighwayEngine Engine { get; }
    public string NodeName { get; }

    private EngineNode(ServiceProvider provider, string nodeName)
    {
        _provider = provider;
        NodeName = nodeName;
        Client = provider.GetRequiredService<IHighwayClient>();
        Engine = provider.GetRequiredService<IHighwayEngine>();
    }

    public static async Task<EngineNode> StartAsync(
        string server, string nodeName, Action<HighwayOptions>? tune = null)
    {
        var services = new ServiceCollection();
        services.AddHighway(o =>
        {
            o.NodeName = nodeName;
            o.Server = server;
            tune?.Invoke(o);
        });

        var provider = services.BuildServiceProvider();
        var node = new EngineNode(provider, nodeName);
        await node.Engine.StartAsync();
        return node;
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAsync();
        await _provider.DisposeAsync();
    }
}

/// <summary>
/// Test classes that assert on <see cref="SubscriberRecorder"/> must share this
/// collection. The recorder is process-global and each such class resets it in
/// its constructor, so running two of them in parallel lets one wipe the other's
/// recorded entries mid-test. A shared collection is xUnit's parallelization
/// unit, so membership serializes them.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SubscriberRecorderCollection
{
    public const string Name = "SubscriberRecorder";
}

/// <summary>Records subscriber invocations across all engines in the process.</summary>
internal static class SubscriberRecorder
{
    private static ConcurrentQueue<string> _events = new();

    public static void Record(string entry) => _events.Enqueue(entry);

    public static void Reset() => _events = new ConcurrentQueue<string>();

    public static int CountEntries(string entry) => _events.Count(e => e == entry);

    /// <summary>Polls until the predicate holds or the timeout elapses.</summary>
    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}

// =============================================================================
// RPC fixtures
// =============================================================================

[Service("it.echo")]
public sealed class ItEchoRequest : IReturn<ItEchoResponse>
{
    public string? Value { get; set; }
}

public sealed class ItEchoResponse : Output
{
    public string? Value { get; set; }
}

public sealed class ItEchoService : AsyncService<ItEchoRequest, ItEchoResponse>
{
    public override Task<ItEchoResponse> ExecuteAsync(ItEchoRequest request, CancellationToken ct = default)
        => Task.FromResult(new ItEchoResponse
        {
            Value = request.Value,
            StatusCode = StatusCodes.Status200OK,
        });
}

[Service("it.slow")]
public sealed class ItSlowRequest : IReturn<ItSlowResponse>
{
    public int DelayMs { get; set; }
}

public sealed class ItSlowResponse : Output
{
    public string? Marker { get; set; }
}

public sealed class ItSlowService : AsyncService<ItSlowRequest, ItSlowResponse>
{
    public override async Task<ItSlowResponse> ExecuteAsync(ItSlowRequest request, CancellationToken ct = default)
    {
        await Task.Delay(request.DelayMs, ct);
        return new ItSlowResponse { Marker = "slow-done", StatusCode = StatusCodes.Status200OK };
    }
}

/// <summary>Implements IReturn but has NO [Service] attribute — never registered.</summary>
public sealed class UnregisteredRequest : IReturn<ItEchoResponse>
{
    public string? Value { get; set; }
}

// =============================================================================
// Pub/Sub fixtures
// =============================================================================

[Channel("it.events")]
public sealed class ItEvent : IPublish
{
    public string? Data { get; set; }
}

/// <summary>First local subscriber — records "A:{Data}".</summary>
public sealed class ItSubscriberA : ISubscribe<ItEvent>
{
    public Task SubscribeAsync(ItEvent message, CancellationToken ct = default)
    {
        SubscriberRecorder.Record($"A:{message.Data}");
        return Task.CompletedTask;
    }
}

/// <summary>Second local subscriber — records "B:{Data}" (fan-out within a node).</summary>
public sealed class ItSubscriberB : ISubscribe<ItEvent>
{
    public Task SubscribeAsync(ItEvent message, CancellationToken ct = default)
    {
        SubscriberRecorder.Record($"B:{message.Data}");
        return Task.CompletedTask;
    }
}

[Channel("it.failover")]
public sealed class ItFailEvent : IPublish
{
    public string? Data { get; set; }
}

/// <summary>Always throws — must not abort its sibling.</summary>
public sealed class ItFailingSubscriber : ISubscribe<ItFailEvent>
{
    public Task SubscribeAsync(ItFailEvent message, CancellationToken ct = default)
        => throw new InvalidOperationException("subscriber boom");
}

/// <summary>Surviving sibling — records "S:{Data}".</summary>
public sealed class ItSurvivorSubscriber : ISubscribe<ItFailEvent>
{
    public Task SubscribeAsync(ItFailEvent message, CancellationToken ct = default)
    {
        SubscriberRecorder.Record($"S:{message.Data}");
        return Task.CompletedTask;
    }
}
