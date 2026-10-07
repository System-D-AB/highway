using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- contracts ------------------------------------------------------------------------------------------

[Service("dl074.work")]
public sealed class DlWorkRequest : IReturn<DlResponse>
{
    public string Key { get; set; } = "";
    public bool Hang { get; set; }
}

[Service("dl074.outer")]
public sealed class DlOuterRequest : IReturn<DlResponse>
{
    public string Key { get; set; } = "";
}

[Service("dl074.inner")]
public sealed class DlInnerRequest : IReturn<DlResponse>
{
    public string Key { get; set; } = "";
}

/// <summary>A request no handler anywhere implements: nobody will ever serve it.</summary>
[Service("dl074.nobody")]
public sealed class DlNobodyRequest : IReturn<DlResponse>;

public sealed class DlResponse : Output
{
    public string Echo { get; set; } = "";
}

public static class DlProbe
{
    public static readonly ConcurrentDictionary<string, int> Executions = new();
    public static readonly ConcurrentDictionary<string, TimeSpan> CancelledAfter = new();
    public static readonly SemaphoreSlim HangRelease = new(0);
}

public sealed class DlWorkService : AsyncService<DlWorkRequest, DlResponse>
{
    public override async Task<DlResponse> ExecuteAsync(DlWorkRequest request, CancellationToken ct = default)
    {
        DlProbe.Executions.AddOrUpdate(request.Key, 1, (_, n) => n + 1);
        if (request.Hang)
            await DlProbe.HangRelease.WaitAsync(CancellationToken.None);   // ignores its token, like a stuck driver call
        return new DlResponse { StatusCode = StatusCodes.Status200OK, Echo = request.Key };
    }
}

public sealed class DlOuterService(IHighwayClient client) : AsyncService<DlOuterRequest, DlResponse>
{
    public override async Task<DlResponse> ExecuteAsync(DlOuterRequest request, CancellationToken ct = default)
    {
        // No timeout given: the inner call must inherit this handler's remaining time (074, D6).
        var inner = await client.ExecuteAsync(new DlInnerRequest { Key = request.Key }, CancellationToken.None);
        return new DlResponse { StatusCode = inner.StatusCode, Echo = inner.Echo };
    }
}

public sealed class DlInnerService : AsyncService<DlInnerRequest, DlResponse>
{
    public override async Task<DlResponse> ExecuteAsync(DlInnerRequest request, CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);   // honours its token
        }
        catch (OperationCanceledException)
        {
            DlProbe.CancelledAfter[request.Key] = started.Elapsed;
            throw;
        }
        return new DlResponse { StatusCode = StatusCodes.Status200OK, Echo = request.Key };
    }
}

/// <summary>
/// 074 Part A end to end, with real engines and a real broker: the failure rows the feature exists for.
/// </summary>
public class RpcDeadlineIntegrationTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly List<EngineNode> _nodes = [];

    public void Dispose()
    {
        foreach (var n in _nodes) n.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.Dispose();
    }

    private async Task<EngineNode> Worker(string name, Action<HighwayOptions>? tune = null)
    {
        var node = await EngineNode.StartAsync(_server.ConnectionString, name, tune);
        _nodes.Add(node);
        return node;
    }

    /// <summary>A process that calls but hosts nothing (Declared hosting with no host assemblies).</summary>
    private async Task<EngineNode> Caller(string name = "caller")
        => await Worker(name, o => o.HostingMode = HostingMode.Declared);

    private async Task<long> StatAsync(string name, string field)
    {
        var db = (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();
        var fields = (RedisResult[])(await db.ExecuteAsync("HW.STATS", name))!;
        for (var i = 0; i + 1 < fields.Length; i += 2)
            if (fields[i].ToString() == field) return long.Parse(fields[i + 1].ToString()!);
        return -1;
    }

    [Fact]
    public async Task ACallToAServiceNobodyServes_Fails503AtOnce()
    {
        var caller = await Caller();
        var sw = Stopwatch.StartNew();

        var response = await caller.Client.ExecuteAsync(new DlNobodyRequest());

        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        response.Error!.Code.Should().Be("NO_WORKERS");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "fail fast — not after the 30 s CallTimeout");
    }

    [Fact]
    public async Task HungHandlers_AreCutOffAtTheirCallersDeadline_AndTheServiceKeepsAnswering()
    {
        // The failure this guards: every worker slot taken by a handler that never returns. Before 074 the service stopped
        // answering until a restart; now each hung call ends at its caller's deadline and the slot comes back.
        await Worker("worker", o =>
        {
            o.WorkerConcurrency = 2;
            o.AbandonGrace = TimeSpan.FromMilliseconds(200);
            o.MaxAbandonedHandlers = 4;
        });
        var caller = await Caller();
        try
        {
            var hung = Enumerable.Range(0, 2).Select(i =>
                caller.Client.ExecuteAsync(new DlWorkRequest { Key = $"hang-{i}", Hang = true }, TimeSpan.FromMilliseconds(700))).ToArray();
            var hungResults = await Task.WhenAll(hung);
            hungResults.Should().AllSatisfy(r => r.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout));

            var live = await caller.Client.ExecuteAsync(new DlWorkRequest { Key = "after" }, TimeSpan.FromSeconds(10));

            live.StatusCode.Should().Be(StatusCodes.Status200OK, "the service answers new calls without a restart");
            live.Echo.Should().Be("after");
            DlProbe.Executions["hang-0"].Should().Be(1, "an abandoned request is acknowledged, never run again");
            DlProbe.Executions["hang-1"].Should().Be(1);
        }
        finally
        {
            DlProbe.HangRelease.Release(2);
        }
    }

    [Fact]
    public async Task ABacklogNobodyWaitsFor_IsNeverRun_WhenTheWorkerReturns()
    {
        await using (await EngineNode.StartAsync(_server.ConnectionString, "worker-first-life")) { }   // served once, then gone
        var caller = await Caller();

        var calls = Enumerable.Range(0, 5).Select(i =>
            caller.Client.ExecuteAsync(new DlWorkRequest { Key = $"backlog-{i}" }, TimeSpan.FromMilliseconds(300))).ToArray();
        (await Task.WhenAll(calls)).Should().AllSatisfy(r => r.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout));

        await Worker("worker-second-life");
        var fresh = await caller.Client.ExecuteAsync(new DlWorkRequest { Key = "fresh" }, TimeSpan.FromSeconds(10));
        fresh.StatusCode.Should().Be(StatusCodes.Status200OK);
        await Task.Delay(500);

        Enumerable.Range(0, 5).Should().AllSatisfy(i => DlProbe.Executions.ContainsKey($"backlog-{i}").Should().BeFalse(
            "their callers stopped waiting before any worker could take them"));
        (await StatAsync("dl074.work", "expired")).Should().Be(5);
    }

    [Fact]
    public async Task AnInnerCall_EndsWithItsOuterCallersDeadline()
    {
        await Worker("worker");
        var caller = await Caller();
        var sw = Stopwatch.StartNew();

        var response = await caller.Client.ExecuteAsync(new DlOuterRequest { Key = "nested" }, TimeSpan.FromSeconds(1));

        response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        (await SubscriberRecorder.WaitForAsync(() => DlProbe.CancelledAfter.ContainsKey("nested"), TimeSpan.FromSeconds(5)))
            .Should().BeTrue("the inner handler is cancelled with the outer caller's deadline");
        DlProbe.CancelledAfter["nested"].Should().BeLessThan(TimeSpan.FromSeconds(2),
            "not after its own 30 s default — an inner call never outlives the caller waiting on the outer one");
    }

    [Fact]
    public async Task ACallerThatGivesUp_WithdrawsItsQueuedRequest()
    {
        await using (await EngineNode.StartAsync(_server.ConnectionString, "worker-first-life")) { }   // served, then gone
        var caller = await Caller();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => caller.Client.ExecuteAsync(new DlWorkRequest { Key = "abandoned-by-user" }, TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await SubscriberRecorder.WaitForAsync(() => StatAsync("dl074.work", "cancelled").GetAwaiter().GetResult() == 1, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("the client withdrew the request it no longer wants");
        (await StatAsync("dl074.work", "queueDepth")).Should().Be(0);
    }
}
