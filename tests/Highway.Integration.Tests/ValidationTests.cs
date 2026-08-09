using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Integration tests for input validation and RESP error handling (Requirement 14).
/// Verifies that malformed commands are rejected with clear errors and no state corruption.
/// </summary>
public class ValidationTests : IDisposable
{
    private readonly HighwayTestServer _server;
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public ValidationTests()
    {
        // Use a small MaxPayloadBytes for oversized payload tests
        _server = new HighwayTestServer(maxPayloadBytes: 64);
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────
    // Wrong arity tests (too few arguments)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HwCall_WrongArity_ReturnsError()
    {
        // HW.CALL needs 3 args: service, requestId, payload
        var act = () => _db.Execute("HW.CALL", "service-only");
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwReply_WrongArity_ReturnsError()
    {
        // HW.REPLY needs 2 args: requestId, payload
        var act = () => _db.Execute("HW.REPLY", "req-1");
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwDequeue_WrongArity_ReturnsError()
    {
        // HW.DEQUEUE needs 2 args: service, nodeId
        var act = () => _db.Execute("HW.DEQUEUE", "service-only");
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwAck_WrongArity_ReturnsError()
    {
        // HW.ACK needs 3 args: service, nodeId, requestId
        var act = () => _db.Execute("HW.ACK", "service", "node");
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwSubscribe_WrongArity_ReturnsError()
    {
        // HW.SUBSCRIBE needs 2 args: channel, group
        var act = () => _db.Execute("HW.SUBSCRIBE", "channel-only");
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwUnsubscribe_WrongArity_ReturnsError()
    {
        // HW.UNSUBSCRIBE needs 2 args: channel, group
        var act = () => _db.Execute("HW.UNSUBSCRIBE", "channel-only");
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwPublish_WrongArity_ReturnsError()
    {
        // HW.PUBLISH needs 2 args: channel, payload
        var act = () => _db.Execute("HW.PUBLISH", "channel-only");
        act.Should().Throw<RedisServerException>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Blank identifier tests
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "req-1", "payload")]
    public void HwCall_BlankService_ReturnsError(string service, string reqId, string payload)
    {
        var act = () => _db.Execute("HW.CALL", service, reqId, payload);
        act.Should().Throw<RedisServerException>();
    }

    [Theory]
    [InlineData("svc", "", "payload")]
    public void HwCall_BlankRequestId_ReturnsError(string service, string reqId, string payload)
    {
        var act = () => _db.Execute("HW.CALL", service, reqId, payload);
        act.Should().Throw<RedisServerException>();
    }

    [Theory]
    [InlineData("", "group")]
    public void HwSubscribe_BlankChannel_ReturnsError(string channel, string group)
    {
        var act = () => _db.Execute("HW.SUBSCRIBE", channel, group);
        act.Should().Throw<RedisServerException>();
    }

    [Theory]
    [InlineData("channel", "")]
    public void HwSubscribe_BlankGroup_ReturnsError(string channel, string group)
    {
        var act = () => _db.Execute("HW.SUBSCRIBE", channel, group);
        act.Should().Throw<RedisServerException>();
    }

    [Theory]
    [InlineData("", "node-1")]
    public void HwDequeue_BlankService_ReturnsError(string service, string nodeId)
    {
        var act = () => _db.Execute("HW.DEQUEUE", service, nodeId);
        act.Should().Throw<RedisServerException>();
    }

    [Theory]
    [InlineData("svc", "")]
    public void HwDequeue_BlankNodeId_ReturnsError(string service, string nodeId)
    {
        var act = () => _db.Execute("HW.DEQUEUE", service, nodeId);
        act.Should().Throw<RedisServerException>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Oversized payload tests (server configured with 64-byte max)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HwCall_OversizedPayload_ReturnsError()
    {
        var oversized = new string('x', 100); // > 64 bytes
        var act = () => _db.Execute("HW.CALL", "svc", "req-big", oversized);
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwReply_OversizedPayload_ReturnsError()
    {
        var oversized = new string('x', 100); // > 64 bytes
        var act = () => _db.Execute("HW.REPLY", "req-big", oversized);
        act.Should().Throw<RedisServerException>();
    }

    [Fact]
    public void HwPublish_OversizedPayload_ReturnsError()
    {
        var oversized = new string('x', 100); // > 64 bytes
        var act = () => _db.Execute("HW.PUBLISH", "channel", oversized);
        act.Should().Throw<RedisServerException>();
    }
}
