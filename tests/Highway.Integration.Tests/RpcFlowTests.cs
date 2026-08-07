using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Integration tests for the RPC command flow:
/// HW.CALL → HW.DEQUEUE → HW.REPLY → GET reply → HW.ACK
/// </summary>
public class RpcFlowTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
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
    public void HwDequeue_ReturnsEnqueuedRequest()
    {
        _db.Execute("HW.CALL", "payments.process", "req-100", "{\"amount\":99.99}");

        var result = _db.Execute("HW.DEQUEUE", "payments.process", "node-1");

        result.IsNull.Should().BeFalse();
        var arr = (RedisResult[])result!;
        arr.Should().HaveCount(2);
        ((string)arr[0]!).Should().Be("req-100");
        ((string)arr[1]!).Should().Be("{\"amount\":99.99}");
    }

    [Fact]
    public void HwReply_WritesReplySlot()
    {
        _db.Execute("HW.REPLY", "req-200", "{\"status\":\"ok\"}");

        var reply = _db.StringGet("hw:rep:req-200");
        reply.ToString().Should().Be("{\"status\":\"ok\"}");
    }

    [Fact]
    public void FullRpcRoundTrip_CallDequeueReplyAck()
    {
        // 1. Caller enqueues
        var callResult = _db.Execute("HW.CALL", "inventory.check", "req-rt-1", "{\"sku\":\"ABC\"}");
        callResult.ToString().Should().Be("OK");

        // 2. Worker dequeues
        var deqResult = _db.Execute("HW.DEQUEUE", "inventory.check", "worker-1");
        deqResult.IsNull.Should().BeFalse();
        var arr = (RedisResult[])deqResult!;
        ((string)arr[0]!).Should().Be("req-rt-1");
        ((string)arr[1]!).Should().Be("{\"sku\":\"ABC\"}");

        // 3. Worker replies
        var replyResult = _db.Execute("HW.REPLY", "req-rt-1", "{\"inStock\":true}");
        replyResult.ToString().Should().Be("OK");

        // 4. Caller retrieves reply via stock GET
        var replyPayload = _db.StringGet("hw:rep:req-rt-1");
        replyPayload.ToString().Should().Be("{\"inStock\":true}");

        // 5. Worker acknowledges
        var ackResult = _db.Execute("HW.ACK", "inventory.check", "worker-1", "req-rt-1");
        ackResult.ToString().Should().Be("OK");
    }

    [Fact]
    public void HwDequeue_EmptyQueue_ReturnsNil()
    {
        var result = _db.Execute("HW.DEQUEUE", "nonexistent.service", "node-1");
        result.IsNull.Should().BeTrue();
    }

    [Fact]
    public void HwAck_Idempotent_ReturnsOk()
    {
        // ACK for unknown requestId still returns OK
        var result = _db.Execute("HW.ACK", "some.service", "node-1", "never-dequeued");
        result.ToString().Should().Be("OK");
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

        // 3 concurrent dequeue loops — retry on transient transaction failure
        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, consumerCount).Select(nodeIdx => Task.Run(() =>
        {
            using var conn = ConnectionMultiplexer.Connect(_server.ConnectionString);
            var db = conn.GetDatabase();

            while (true)
            {
                RedisResult? result = null;
                // Retry on transient transaction failures (per design: client retries on ERR Transaction failed)
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        result = db.Execute("HW.DEQUEUE", service, $"node-{nodeIdx}");
                        break;
                    }
                    catch (RedisServerException ex) when (ex.Message.Contains("Transaction failed"))
                    {
                        Thread.Sleep(10);
                    }
                }

                if (result == null || result.IsNull) break;

                var arr = (RedisResult[])result!;
                claimed.Add((string)arr[0]!);
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
