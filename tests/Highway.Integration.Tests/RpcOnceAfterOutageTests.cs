using System.Collections.Concurrent;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

[Service("it.once")]
public sealed class ItOnceRequest : IReturn<ItOnceResponse>
{
    public string? Key { get; set; }
}

public sealed class ItOnceResponse : Output;

/// <summary>Counts executions per key, so a test can see how often one call really ran.</summary>
public sealed class ItOnceService : AsyncService<ItOnceRequest, ItOnceResponse>
{
    internal static readonly ConcurrentDictionary<string, int> Executions = new();

    public override Task<ItOnceResponse> ExecuteAsync(ItOnceRequest request, CancellationToken ct = default)
    {
        Executions.AddOrUpdate(request.Key ?? "?", 1, (_, n) => n + 1);
        return Task.FromResult(new ItOnceResponse { StatusCode = StatusCodes.Status200OK });
    }
}

/// <summary>
/// 074 (3.0; carried from 2.5.6), end to end against a real worker: the failure mode, replayed. The service's worker is down
/// (or every handler slot is hung); callers keep re-sending their pending calls — a client
/// re-sends on a connection blip, same request id each time — and then the worker comes back.
/// </summary>
public class RpcOnceAfterOutageTests : IAsyncLifetime
{
    private HighwayTestServer _server = null!;
    private readonly List<EngineNode> _nodes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
        _server?.Dispose();
    }

    private static string Envelope(string key, DateTime createdUtc)
        => $$$"""{"v":1,"src":"web-1","ts":"{{{createdUtc:O}}}","body":{"key":"{{{key}}}"}}""";

    private async Task WorkerComesBack()
        => _nodes.Add(await EngineNode.StartAsync(_server.ConnectionString, "worker-" + Guid.NewGuid().ToString("N")[..6]));

    [Fact]
    public async Task ACallReSentManyTimesDuringAnOutage_RunsOnce_WhenTheWorkerReturns()
    {
        _server = new HighwayTestServer();
        var key = "cart-" + Guid.NewGuid().ToString("N");
        var db = (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();
        var payload = Envelope(key, DateTime.UtcNow);

        for (var i = 0; i < 12; i++)   // the client's re-sends, original request id every time
            (await db.ExecuteAsync("HW.CALL", "it.once", "req-" + key, payload)).ToString().Should().Be("OK");

        await WorkerComesBack();

        (await SubscriberRecorder.WaitForAsync(() => ItOnceService.Executions.ContainsKey(key), TimeSpan.FromSeconds(10)))
            .Should().BeTrue("the call itself must still run");
        await Task.Delay(1_500);   // give any surviving copy time to run too
        ItOnceService.Executions[key].Should().Be(1, "one call, re-sent twelve times, is still one call");
    }
}
