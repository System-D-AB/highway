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
/// <c>HW.SEND</c> directly, so client-side alone is not enough.</para>
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
    public void HwSend_AtSignInQueueName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Send("orders@billing", "m-1", "body"u8.ToArray()));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    [Fact]
    public void HwClaimQ_AtSignInQueueName_Succeeds()
    {
        // HW.CLAIM Q accepts @ because derived group queues ({channel}@{group}) are
        // consumed through this command (feature 018). Returns an empty array (empty queue).
        var result = _db.Execute("HW.CLAIM", "Q", "orders@billing", "node-1");
        result.IsNull.Should().BeFalse("nothing due is an empty array in 5.0, not nil");
        ((RedisResult[])result!).Should().BeEmpty("the queue is empty but the command must not reject the name");
    }

    [Fact]
    public void HwAck_GroupQueueClaim_Succeeds()
    {
        // Derived group queues ({channel}@{group}) are acknowledged like any other claim
        // (feature 018). Since 5.0 HW.ACK takes only the claim's token, so the '@' name is
        // never re-validated on settle — the claim that produced the token is what accepted it.
        _db.Subscribe("orders.placed", "billing");
        _db.Execute("HW.PUBLISH", "orders.placed", "payload"u8.ToArray());

        var claim = _db.ClaimQ("orders.placed@billing", "node-1");
        claim.Should().NotBeNull();

        _db.Ack(claim!.Value.Token).Should().Be(1, "the group-queue claim is held and settles");
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
    public void HwSessionSubscription_AtSignInChannelName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Subscribe("events@group", "my-group", "node-1"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    [Fact]
    public void HwSessionSubscription_AtSignInGroupName_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Subscribe("events", "my@group", "node-1"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }

    // -------------------------------------------------------------------------
    // Valid names (without @) remain unaffected
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidQueueName_WithoutAtSign_Succeeds()
    {
        _db.Send("orders.placed", "m-1", "body"u8.ToArray())
            .ToString().Should().Be("OK");
    }

    [Fact]
    public void ValidChannelName_WithoutAtSign_Succeeds()
    {
        _db.Subscribe("orders.placed", "billing")
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
    public void HwClaim_AtSignInNodeId_HwInvalidArg()
    {
        var message = ErrorOf(() =>
            _db.Execute("HW.CLAIM", "SVC", "svc", "node@1"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
        message.Should().Contain("@");
    }
}
