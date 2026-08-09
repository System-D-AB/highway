using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 018, Task 0 — <c>@</c> is reserved in queue and channel names.
///
/// <para>Without this, a queue named <c>orders.placed@billing</c> would collide
/// with the <c>billing</c> group of the <c>orders.placed</c> channel once pub/sub
/// unifies onto the queue engine.</para>
///
/// <para>Both enforcement points: client-side scanning (tested in unit tests) and
/// server-side command validation (tested here). A non-Highway client can issue
/// <c>HW.QSEND</c> directly, so server-side alone is not enough.</para>
/// </summary>
public class AtSignReservationTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public AtSignReservationTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    private string ErrorOf(Action act)
    {
        try
        {
            act();
            Assert.Fail("expected a RESP error, but the command succeeded");
            return string.Empty;
        }
        catch (RedisServerException ex)
        {
            return ex.Message;
        }
    }

    // -------------------------------------------------------------------------
    // Server rejects @ in queue names
    // -------------------------------------------------------------------------

    [Fact]
    public void HwQSend_AtSignInQueueName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Execute("HW.QSEND", "orders@billing", "m-1", "body"u8.ToArray()));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    [Fact]
    public void HwQClaim_AtSignInQueueName_Succeeds()
    {
        // HW.QCLAIM accepts @ because derived group queues ({channel}@{group}) are
        // consumed through this command (feature 018). Returns nil (empty queue).
        var result = _db.Execute("HW.QCLAIM", "orders@billing", "node-1");
        result.IsNull.Should().BeTrue("the queue is empty but the command must not reject the name");
    }

    [Fact]
    public void HwQAck_AtSignInQueueName_Succeeds()
    {
        // HW.QACK accepts @ because derived group queues ({channel}@{group}) are
        // acknowledged through this command (feature 018). Returns 0 (not found)
        // because no message was claimed, but the command must not reject the name.
        var result = (int)_db.Execute("HW.QACK", "orders@billing", "node-1", "m-1");
        result.Should().Be(0, "no message was claimed, but the name is valid");
    }

    // -------------------------------------------------------------------------
    // Server rejects @ in channel names
    // -------------------------------------------------------------------------

    [Fact]
    public void HwPublish_AtSignInChannelName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Execute("HW.PUBLISH", "events@group", "payload"u8.ToArray()));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    [Fact]
    public void HwSubscribe_AtSignInChannelName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Execute("HW.SUBSCRIBE", "events@group", "my-group"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    [Fact]
    public void HwSubscribe_AtSignInGroupName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Execute("HW.SUBSCRIBE", "events", "my@group"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    // -------------------------------------------------------------------------
    // Valid names (without @) remain unaffected
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidQueueName_WithoutAtSign_Succeeds()
    {
        _db.Execute("HW.QSEND", "orders.placed", "m-1", "body"u8.ToArray())
            .ToString().Should().Be("OK");
    }

    [Fact]
    public void ValidChannelName_WithoutAtSign_Succeeds()
    {
        _db.Execute("HW.SUBSCRIBE", "orders.placed", "billing")
            .ToString().Should().Be("OK");
        var count = (int)_db.Execute("HW.PUBLISH", "orders.placed", "payload"u8.ToArray());
        count.Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Service, node, and request identifiers also reject @
    // -------------------------------------------------------------------------

    [Fact]
    public void HwCall_AtSignInServiceName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Execute("HW.CALL", "svc@bad", "req-1", "body"u8.ToArray()));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    [Fact]
    public void HwDequeue_AtSignInNodeId_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Execute("HW.DEQUEUE", "svc", "node@1"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }
}
