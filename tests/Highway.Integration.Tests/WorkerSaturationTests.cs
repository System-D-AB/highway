using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- contracts ------------------------------------------------------------

[Service("sat.slow")]
public sealed class SlowRequest : IReturn<SlowResponse>
{
    public string Tag { get; set; } = "";
}

public sealed class SlowResponse : Output { }

/// <summary>
/// Holds every invocation until released, so the concurrency gate can be saturated
/// deliberately rather than by timing luck.
/// </summary>
public sealed class SlowService : AsyncService<SlowRequest, SlowResponse>
{
    public static readonly SemaphoreSlim Release = new(0);
    public static int Entered;

    public override async Task<SlowResponse> ExecuteAsync(SlowRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Entered);
        await Release.WaitAsync(ct);
        return new SlowResponse { StatusCode = StatusCodes.Status200OK };
    }
}

[Queue("sat.queue")]
public sealed record SlowQueueMessage : ISend
{
    public string Tag { get; init; } = "";
}

public sealed class SlowQueueProcessor : IProcess<SlowQueueMessage>
{
    public static readonly SemaphoreSlim Release = new(0);
    public static int Entered;

    public async Task ProcessAsync(SlowQueueMessage message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Entered);
        await Release.WaitAsync(ct);
    }
}

/// <summary>
/// Feature 015 T0 — a worker must never claim work it cannot start.
///
/// <para><b>The property.</b> A node must never hold more claims than it has concurrency
/// slots. A claim starts the lease clock, so a message claimed but unstartable can be
/// redelivered elsewhere while this node still intends to process it — a duplicate produced
/// by load alone, with no failure involved.</para>
///
/// <para><b>These tests were written to prove a defect that does not exist.</b> The two loops
/// order their gate and their claim differently — <c>RpcWorkerLoop</c> dequeues then waits,
/// <c>QueueWorkerLoop</c> waits then claims — and that was read as RPC over-claiming under
/// saturation. It does not: the RPC test observes exactly one claim with one slot. The
/// difference in ordering is real but does not produce the predicted behaviour, and the reason
/// has not been established.</para>
///
/// <para>The tests are kept because the property is worth pinning regardless of which loop
/// implements it how, and because any future unification of the loops must not break it.</para>
///
/// <para>Observability note: the processing list is the claim ledger. Its length is exactly
/// "how many messages this node has taken responsibility for", so asserting on it measures the
/// property directly rather than inferring it from timing.</para>
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class WorkerSaturationTests : IDisposable
{
    private readonly HighwayTestServer _server = new();

    public WorkerSaturationTests()
    {
        SlowService.Entered = 0;
        SlowQueueProcessor.Entered = 0;
    }

    public void Dispose()
    {
        // Let any parked handler finish so the node can drain.
        SlowService.Release.Release(64);
        SlowQueueProcessor.Release.Release(64);
        _server.Dispose();
    }

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task RpcWorker_NeverClaimsMoreThanItCanStart()
    {
        var db = await ConnectAsync();

        // Concurrency 1: exactly one request may be in flight, so at most one may be claimed.
        await using var host = await EngineNode.StartAsync(
            _server.ConnectionString, "sat-rpc-host", o => o.WorkerConcurrency = 1);

        for (var i = 0; i < 4; i++)
        {
            await db.ExecuteAsync("HW.CALL", "sat.slow", $"sat-{i}",
                System.Text.Encoding.UTF8.GetBytes(
                    """{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{"Tag":"x"}}"""));
        }

        // One handler is parked, so the gate is full and no further work may be claimed.
        await WaitForAsync(() => Volatile.Read(ref SlowService.Entered) >= 1);
        await Task.Delay(400);   // give a mis-ordered loop time to over-claim

        var claimed = (long)(await db.ExecuteAsync("LLEN", "hw:svc:sat.slow:proc:sat-rpc-host"));

        // The handler is known to have entered (asserted by the wait above), so this is not
        // vacuous: something was claimed and processed. The property under test is the upper
        // bound.
        Volatile.Read(ref SlowService.Entered).Should().BeGreaterThan(0);
        claimed.Should().BeLessThanOrEqualTo(1,
            "one concurrency slot means at most one claim - more is a message whose lease is " +
            "running while the node cannot start it");
    }

    [Fact]
    public async Task QueueWorker_NeverClaimsMoreThanItCanStart()
    {
        var db = await ConnectAsync();

        await using var host = await EngineNode.StartAsync(
            _server.ConnectionString, "sat-q-host", o => o.WorkerConcurrency = 1);

        for (var i = 0; i < 4; i++)
        {
            await db.ExecuteAsync("HW.QSEND", "sat.queue", $"satq-{i}",
                System.Text.Encoding.UTF8.GetBytes(
                    """{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{"Tag":"x"}}"""));
        }

        await WaitForAsync(() => Volatile.Read(ref SlowQueueProcessor.Entered) >= 1);
        await Task.Delay(400);

        var claimed = (long)(await db.ExecuteAsync("LLEN", "hw:q:sat.queue:proc:sat-q-host"));

        Volatile.Read(ref SlowQueueProcessor.Entered).Should().BeGreaterThan(0);
        claimed.Should().BeLessThanOrEqualTo(1,
            "the queue loop waits for a slot before claiming - this guards against a future " +
            "refactor harmonising the two loops the wrong way");
    }
}
