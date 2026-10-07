// 074: these tests make raw HW.CALLs to services no session declared, so fail-fast is off for their servers.
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Integration tests for the RPC command flow (protocol 5.0):
/// HW.CALL → HW.CLAIM SVC → HW.REPLY → HW.RESULT → HW.ACK token
/// </summary>
public class RpcFlowTests : IDisposable
{
    private readonly HighwayTestServer _server = new(o => o.RpcFailFast = false);
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public RpcFlowTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void HwCall_EnqueuesRequest_ReturnsOk()
    {
        var result = _db.Execute("HW.CALL", "orders.create", "req-1", "{\"customerId\":42}");
        result.ToString().Should().Be("OK");
    }

    [Fact]
    public void HwClaim_ReturnsEnqueuedRequest()
    {
        _db.Execute("HW.CALL", "payments.process", "req-100", "{\"amount\":99.99}");

        var result = _db.Execute("HW.CLAIM", "SVC", "payments.process", "node-1");

        var rows = (RedisResult[])result!;
        rows.Should().HaveCount(1);
        var row = (RedisResult[])rows[0]!;
        row.Should().HaveCount(4, "an RPC row carries the request's remaining time (074)");
        long.Parse((string)row[3]!).Should().BeInRange(1, 30_000, "a call without TIMEOUT gets the 30 s broker default");
        ((string)row[0]!).Should().NotBeNullOrEmpty("the lease token is the claim's handle");
        ((string)row[1]!).Should().Be("req-100");
        ((string)row[2]!).Should().Be("{\"amount\":99.99}");
    }

    [Fact]
    public void HwReply_WritesReplySlot()
    {
        _db.Execute("HW.REPLY", "req-200", "{\"status\":\"ok\"}");

        _server.Inspect.ReplySlotExists("req-200").Should().BeTrue();
        _db.Result("req-200").ToString().Should().Be("{\"status\":\"ok\"}");
    }

    [Fact]
    public void HwResult_TakesTheReply_ASecondTakeIsNil()
    {
        _db.Execute("HW.REPLY", "req-201", "{\"status\":\"ok\"}");

        _db.Result("req-201").ToString().Should().Be("{\"status\":\"ok\"}");
        _db.Result("req-201").IsNull.Should().BeTrue("HW.RESULT reads and removes in one command");
        _server.Inspect.ReplySlotExists("req-201").Should().BeFalse();
    }

    [Fact]
    public void FullRpcRoundTrip_CallClaimReplyResultAck()
    {
        // 1. Caller enqueues
        var callResult = _db.Execute("HW.CALL", "inventory.check", "req-rt-1", "{\"sku\":\"ABC\"}");
        callResult.ToString().Should().Be("OK");

        // 2. Worker claims
        var claim = _db.ClaimSvc("inventory.check", "worker-1");
        claim.Should().NotBeNull();
        claim!.Value.Id.Should().Be("req-rt-1");
        claim.Value.PayloadText.Should().Be("{\"sku\":\"ABC\"}");

        // 3. Worker replies
        var replyResult = _db.Execute("HW.REPLY", "req-rt-1", "{\"inStock\":true}");
        replyResult.ToString().Should().Be("OK");

        // 4. Caller takes the reply
        var replyPayload = _db.Result("req-rt-1");
        replyPayload.ToString().Should().Be("{\"inStock\":true}");

        // 5. Worker acknowledges by token
        _db.Ack(claim.Value.Token).Should().Be(1);
    }

    [Fact]
    public void HwClaim_EmptyQueue_ReturnsEmptyArray()
    {
        var result = _db.Execute("HW.CLAIM", "SVC", "nonexistent.service", "node-1");
        result.IsNull.Should().BeFalse("nothing due is an empty array in 5.0, not nil");
        ((RedisResult[])result!).Should().BeEmpty();
    }

    [Fact]
    public void HwAck_Idempotent_ASettledTokenAnswersNotHeld()
    {
        // Acknowledging a token that is no longer held is not an error: it answers :0.
        _db.Execute("HW.CALL", "some.service", "req-ack-twice", "p");
        var claim = _db.ClaimSvc("some.service", "node-1")!.Value;

        _db.Ack(claim.Token).Should().Be(1);
        _db.Ack(claim.Token).Should().Be(0);
    }

    [Fact]
    public async Task CompetingConsumers_EachRequestClaimedOnce()
    {
        const int requestCount = 100;
        const int consumerCount = 3;
        const string service = "load.test";

        // Enqueue 100 requests
        for (int i = 0; i < requestCount; i++)
        {
            _db.Execute("HW.CALL", service, $"req-{i}", $"payload-{i}");
        }

        // 3 concurrent claim loops (5.0 has no transient "Transaction failed" to retry)
        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, consumerCount).Select(nodeIdx => Task.Run(() =>
        {
            using var conn = ConnectionMultiplexer.Connect(_server.ConnectionString);
            var db = conn.GetDatabase();

            while (db.ClaimSvc(service, $"node-{nodeIdx}") is { } claim)
            {
                claimed.Add(claim.Id);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Verify all 100 claimed exactly once
        var claimedList = claimed.ToList();
        claimedList.Should().HaveCount(requestCount);
        claimedList.Distinct().Should().HaveCount(requestCount);

        // Verify all request IDs are present
        var expected = Enumerable.Range(0, requestCount).Select(i => $"req-{i}").ToHashSet();
        claimedList.ToHashSet().Should().BeEquivalentTo(expected);
    }
}
