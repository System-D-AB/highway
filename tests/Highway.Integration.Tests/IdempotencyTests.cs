using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- contracts under test -------------------------------------------------

[Service("idem.count")]
[Idempotent(WindowSeconds = 30)]
public sealed class CountedRequest : IReturn<CountedResponse>
{
    public string Tag { get; set; } = "";
}

public sealed class CountedResponse : Output
{
    public int Invocation { get; set; }
}

[Service("plain.count")]
public sealed class PlainRequest : IReturn<CountedResponse>
{
    public string Tag { get; set; } = "";
}

public sealed class CountedService : AsyncService<CountedRequest, CountedResponse>
{
    public static int Invocations;

    public override Task<CountedResponse> ExecuteAsync(CountedRequest request, CancellationToken ct)
        => Task.FromResult(new CountedResponse
        {
            StatusCode = StatusCodes.Status200OK,
            Invocation = Interlocked.Increment(ref Invocations),
        });
}

public sealed class PlainService : AsyncService<PlainRequest, CountedResponse>
{
    public static int Invocations;

    public override Task<CountedResponse> ExecuteAsync(PlainRequest request, CancellationToken ct)
        => Task.FromResult(new CountedResponse
        {
            StatusCode = StatusCodes.Status200OK,
            Invocation = Interlocked.Increment(ref Invocations),
        });
}

/// <summary>
/// Feature 013 Part 3 — <c>[Idempotent]</c>.
///
/// <para>Highway is at-least-once <i>by design</i>: lease recovery redelivers a request
/// whose acknowledgement never arrived, which is the correct behaviour for a durable
/// queue and hands the application a duplicate. These tests drive that path directly.</para>
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class IdempotencyTests : IDisposable
{
    // These tests drive a "redelivery" by repeating HW.CALL with the same request id — a stand-in for lease
    // recovery, which re-enters the request without HW.CALL. The broker itself suppresses a repeated HW.CALL
    // before its deadline (074), which would hide the WORKER behaviour these tests are about
    // ([Idempotent] dedupes; a plain contract runs again), so the broker filter is off for this server.
    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(50);
        o.RpcDuplicateFilter = false;
    });

    public IdempotencyTests()
    {
        CountedService.Invocations = 0;
        PlainService.Invocations = 0;
    }

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    /// <summary>
    /// The guarantee: a redelivered request does not re-run the handler, and the caller
    /// still receives the original response.
    /// </summary>
    [Fact]
    public async Task DuplicateDelivery_DoesNotRerunTheHandler_AndReturnsTheOriginalResponse()
    {
        await using var host = await EngineNode.StartAsync(_server.ConnectionString, "idem-host");
        await using var caller = await EngineNode.StartAsync(_server.ConnectionString, "idem-caller");

        var first = await caller.Client.ExecuteAsync(new CountedRequest { Tag = "a" });
        first.StatusCode.Should().Be(StatusCodes.Status200OK);
        var firstInvocation = first.Invocation;

        CountedService.Invocations.Should().Be(1);

        // Redeliver the *same* request id, exactly as lease recovery would.
        var db = await ConnectAsync();
        var requestId = Guid.NewGuid().ToString("N");

        // Drive the raw path so the request id is under the test's control.
        var envelope = System.Text.Encoding.UTF8.GetBytes(
            """{"v":1,"src":"t","ts":"2026-08-07T00:00:00Z","body":{"Tag":"b"}}""");

        await db.ExecuteAsync("HW.CALL", "idem.count", requestId, envelope);
        await WaitForAsync(() => CountedService.Invocations == 2);
        CountedService.Invocations.Should().Be(2, "a genuinely new request runs");

        // Same id again — this is the duplicate.
        await db.ExecuteAsync("HW.CALL", "idem.count", requestId, envelope);
        await Task.Delay(500);

        CountedService.Invocations.Should().Be(2,
            "the redelivery must be suppressed, not run a third time");

        firstInvocation.Should().Be(1);
    }

    [Fact]
    public async Task ContractWithoutTheAttribute_IsUnchanged()
    {
        await using var host = await EngineNode.StartAsync(_server.ConnectionString, "plain-host");

        var db = await ConnectAsync();
        var requestId = Guid.NewGuid().ToString("N");
        var envelope = System.Text.Encoding.UTF8.GetBytes(
            """{"v":1,"src":"t","ts":"2026-08-07T00:00:00Z","body":{"Tag":"x"}}""");

        await db.ExecuteAsync("HW.CALL", "plain.count", requestId, envelope);
        await WaitForAsync(() => PlainService.Invocations == 1);

        await db.ExecuteAsync("HW.CALL", "plain.count", requestId, envelope);
        await WaitForAsync(() => PlainService.Invocations == 2);

        PlainService.Invocations.Should().Be(2,
            "without [Idempotent] a redelivery runs again, exactly as before");
    }

    /// <summary>
    /// The deduplication marker must expire. A dedup table that grows forever is the same
    /// class of defect as the recorder-name leak feature 011's first run exposed.
    /// </summary>
    [Fact]
    public async Task DeduplicationState_Expires()
    {
        await using var host = await EngineNode.StartAsync(_server.ConnectionString, "idem-ttl");

        var db = await ConnectAsync();
        var requestId = Guid.NewGuid().ToString("N");
        var envelope = System.Text.Encoding.UTF8.GetBytes(
            """{"v":1,"src":"t","ts":"2026-08-07T00:00:00Z","body":{"Tag":"ttl"}}""");

        await db.ExecuteAsync("HW.CALL", "idem.count", requestId, envelope);
        await WaitForAsync(() => CountedService.Invocations == 1);

        // 5.0: the marker is reached through HW.IDEM, not stock keys. The handler already holds (or has
        // settled) the marker, so BEGIN cannot claim it — it answers BUSY until END lands, then DONE.
        RedisResult begin = RedisResult.Create(RedisValue.Null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            begin = await db.ExecuteAsync("HW.IDEM", "BEGIN", "idem.count", requestId, "30");
            if (begin.Resp2Type == ResultType.Array) break;
            await Task.Delay(25);
        }
        begin.Resp2Type.Should().Be(ResultType.Array, "the completed run's marker holds its result: [DONE, bytes]");
        ((string)((RedisResult[])begin!)[0]!).Should().Be("DONE");

        // Its expiry is the idempotency window (read through the test inspector; no wire TTL read in 5.0).
        var framed = _server.Inspect.KvGet($"hw:idem:idem.count:{requestId}");
        framed.Should().NotBeNull();
        var expiresAt = Highway.Server.Storage.ExpiryFraming.TryReadExpiry(framed!);
        expiresAt.Should().NotBeNull("the marker must expire rather than accumulate forever");
        var ttl = TimeSpan.FromTicks(expiresAt!.Value - DateTime.UtcNow.Ticks);
        ttl.Should().BeGreaterThan(TimeSpan.Zero);
        ttl.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60),
            "the window is [Idempotent(WindowSeconds = 30)], raised for RPC to twice the call timeout (074: 2 × 30 s) so a " +
            "caller retrying after a timeout still finds the stored answer");
        ttl.Should().BeGreaterThan(TimeSpan.FromSeconds(30), "the RPC floor applies");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }
}
