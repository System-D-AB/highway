using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Wire;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 005 Task 14 — resilience and lifecycle: backstop-only operation,
/// graceful drain, engine state machine, and server-restart tolerance.
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class ResilienceIntegrationTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly List<EngineNode> _nodes = [];

    public ResilienceIntegrationTests()
    {
        SubscriberRecorder.Reset();
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
            node.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.Dispose();
    }

    private async Task<EngineNode> StartNodeAsync(string name, Action<Highway.Client.HighwayOptions>? tune = null)
    {
        var node = await EngineNode.StartAsync(_server.ConnectionString, name, tune);
        _nodes.Add(node);
        return node;
    }

    [Fact]
    public async Task DoorbellsDisabled_RpcAndPubSub_StillComplete()
    {
        // Backstop-only path: no doorbell subscriptions, everything rides the sweep.
        var caller = await StartNodeAsync("no-doorbell-node", o =>
        {
            o.DoorbellsEnabled = false;
            o.BackstopInterval = TimeSpan.FromMilliseconds(100);
        });

        var response = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "backstop-echo" });
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Value.Should().Be("backstop-echo");

        await caller.Client.PublishAsync(new ItEvent { Data = "backstop-pubsub" });
        var delivered = await SubscriberRecorder.WaitForAsync(() =>
            SubscriberRecorder.CountEntries("A:backstop-pubsub") >= 1,
            TimeSpan.FromSeconds(10));
        delivered.Should().BeTrue("pub/sub must complete via the backstop sweep alone");
    }

    [Fact]
    public async Task GracefulShutdown_DrainsInFlightWork()
    {
        var node = await StartNodeAsync("drain-node", o => o.DrainTimeout = TimeSpan.FromSeconds(5));

        // Seed work over raw RESP so the drain is observable server-side without
        // racing the caller's doorbell against connection disposal.
        var envelope = HighwayJson.EncodeEnvelope("drain-seed", new ItSlowRequest { DelayMs = 600 });
        using (var raw = ConnectionMultiplexer.Connect(_server.ConnectionString))
        {
            raw.GetDatabase().Execute("HW.CALL", "it.slow", "drain-req-1", envelope);
        }

        // Let the worker claim it, then stop: the drain must wait for the
        // in-flight handler and the reply must still be written.
        await Task.Delay(200);
        await node.Engine.StopAsync();
        _nodes.Remove(node);

        using (var raw = ConnectionMultiplexer.Connect(_server.ConnectionString))
        {
            var slot = raw.GetDatabase().StringGet("hw:rep:drain-req-1");
            slot.HasValue.Should().BeTrue(
                "graceful shutdown must drain in-flight work — the reply was written during the drain");
        }
    }

    [Fact]
    public async Task ServerRestart_EngineRecovers_AndCompletesNewCalls()
    {
        var caller = await StartNodeAsync("restart-node");

        var before = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "before-restart" });
        before.Value.Should().Be("before-restart");

        _server.Restart();

        // SE.Redis auto-reconnects (ClientReconnectTests proves the mechanism);
        // give it time to re-establish before the next call.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var after = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "after-restart" });
        after.StatusCode.Should().Be(StatusCodes.Status200OK);
        after.Value.Should().Be("after-restart", "the engine must recover after a server restart");
    }

    [Fact]
    public async Task EngineLifecycle_StateTransitions_AndDoubleStartThrows()
    {
        var node = await StartNodeAsync("lifecycle-node");

        node.Engine.State.Should().Be(EngineState.Running);

        var doubleStart = () => node.Engine.StartAsync();
        await doubleStart.Should().ThrowAsync<InvalidOperationException>();

        await node.Engine.StopAsync();
        node.Engine.State.Should().Be(EngineState.Stopped);

        // Double stop is a no-op.
        await node.Engine.StopAsync();
        node.Engine.State.Should().Be(EngineState.Stopped);

        _nodes.Remove(node);
    }
}
