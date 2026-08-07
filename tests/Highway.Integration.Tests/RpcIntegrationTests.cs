using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 005 Task 12 — RPC end-to-end through real engines and a real
/// embedded server. Every engine scans this assembly, so every node hosts the
/// it.* services; correctness is asserted through caller-side correlation.
/// </summary>
public class RpcIntegrationTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly List<EngineNode> _nodes = [];

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
    public async Task ExecuteAsync_RoundTrip_ReturnsTypedResponse()
    {
        var caller = await StartNodeAsync("rpc-caller");
        await StartNodeAsync("rpc-host");

        var response = await caller.Client.ExecuteAsync(
            new ItEchoRequest { Value = "hello-wire" },
            CancellationToken.None);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Value.Should().Be("hello-wire");
    }

    [Fact]
    public async Task ExecuteAsync_UnregisteredRequestType_Returns404Data_WithoutNetwork()
    {
        var caller = await StartNodeAsync("rpc-404");

        var response = await caller.Client.ExecuteAsync(new UnregisteredRequest { Value = "x" });

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be("SERVICE_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_SlowService_TimesOutWith504Data()
    {
        var caller = await StartNodeAsync("rpc-timeout-caller", o =>
        {
            o.CallTimeout = TimeSpan.FromMilliseconds(500);
        });
        // Abandon the still-running slow handler quickly so the test stays fast.
        await StartNodeAsync("rpc-timeout-host", o => o.DrainTimeout = TimeSpan.FromMilliseconds(200));

        var response = await caller.Client.ExecuteAsync(new ItSlowRequest { DelayMs = 3000 });

        response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be("CALL_TIMEOUT");
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_ThrowsOperationCanceled()
    {
        var caller = await StartNodeAsync("rpc-cancel", o => o.CallTimeout = TimeSpan.FromSeconds(30));
        await StartNodeAsync("rpc-cancel-host", o => o.DrainTimeout = TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => caller.Client.ExecuteAsync(new ItSlowRequest { DelayMs = 3000 }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConcurrentCalls_CorrelateCorrectly()
    {
        var caller = await StartNodeAsync("rpc-concurrent");
        await StartNodeAsync("rpc-concurrent-host");

        var tasks = Enumerable.Range(0, 100)
            .Select(i => caller.Client.ExecuteAsync(new ItEchoRequest { Value = $"msg-{i}" }))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        for (var i = 0; i < 100; i++)
        {
            responses[i].StatusCode.Should().Be(StatusCodes.Status200OK);
            responses[i].Value.Should().Be($"msg-{i}", "each call must receive its own response");
        }
    }

    [Fact]
    public async Task CompetingConsumers_TwoHosts_AllCallsSucceedWithZeroLoss()
    {
        var caller = await StartNodeAsync("rpc-compete-caller");
        await StartNodeAsync("rpc-compete-host-1");
        await StartNodeAsync("rpc-compete-host-2");

        // Both hosts dequeue the same service queue; server-side partitioning is
        // proven in the 004 suite. At the client level the property is: every
        // call gets exactly one correct response — zero loss, zero duplicates.
        var tasks = Enumerable.Range(0, 60)
            .Select(i => caller.Client.ExecuteAsync(new ItEchoRequest { Value = $"compete-{i}" }))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        responses.Should().AllSatisfy(r => r.StatusCode.Should().Be(StatusCodes.Status200OK));
        responses.Select(r => r.Value).Should().BeEquivalentTo(
            Enumerable.Range(0, 60).Select(i => $"compete-{i}"));
    }
}
