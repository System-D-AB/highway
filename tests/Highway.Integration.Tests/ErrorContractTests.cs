using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 8 — Requirement 2 (distinguishable command errors) and
/// Requirement 3 (identifier validation / delimiter safety).
///
/// Classification rule a client implements:
///   message starts with "ERR HW_"    → permanent failure
///   message starts with "NOTPRIMARY" → failover — retry against the new primary
///   anything else                    → permanent
/// (Protocol 5.0 dropped the Garnet-era transient "ERR Transaction failed." — nothing emits it.)
///
/// Group names reach the broker inside <c>HW.SESSION</c>'s JSON (was <c>HW.SUBSCRIBE channel group</c>),
/// so the group-position cases validate the subscription a session declares.
/// </summary>
public class ErrorContractTests : IDisposable
{
    private readonly HighwayTestServer _server = new(maxPayloadBytes: 64);
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public ErrorContractTests()
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
    // HW_INVALID_ARG — blank identifiers in every position
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("HW.CALL", "", "req-1", "payload")]
    [InlineData("HW.CALL", "svc", "", "payload")]
    [InlineData("HW.REPLY", "", "payload", null)]
    [InlineData("HW.SEND", "", "msg-1", "payload")]
    [InlineData("HW.SEND", "q", "", "payload")]
    [InlineData("HW.CLAIM", "SVC", "", "node-1")]
    [InlineData("HW.CLAIM", "SVC", "svc", "")]
    [InlineData("HW.CLAIM", "Q", "", "node-1")]
    [InlineData("HW.CLAIM", "Q", "q", "")]
    [InlineData("HW.SESSION", "", """{"subscriptions":[]}""", null)]
    [InlineData("HW.SESSION", "node-1", """{"subscriptions":[{"channel":"","group":"grp"}]}""", null)]
    [InlineData("HW.SESSION", "node-1", """{"subscriptions":[{"channel":"ch","group":""}]}""", null)]
    [InlineData("HW.UNSUBSCRIBE", "", "grp", null)]
    [InlineData("HW.UNSUBSCRIBE", "ch", "", null)]
    [InlineData("HW.PUBLISH", "", "payload", null)]
    public void BlankIdentifier_EveryPosition_HwInvalidArg(string command, string arg1, string arg2, string? arg3)
    {
        var message = ErrorOf(() =>
        {
            if (arg3 is null) _db.Execute(command, arg1, arg2);
            else _db.Execute(command, arg1, arg2, arg3);
        });

        message.Should().StartWith("ERR HW_INVALID_ARG");
    }

    // -------------------------------------------------------------------------
    // HW_INVALID_TOKEN — a settle whose token does not parse (5.0). A token that
    // parses but is not held is :0, not an error.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("HW.ACK", "")]
    [InlineData("HW.ACK", "not-a-token")]
    [InlineData("HW.ACK", "12")]
    [InlineData("HW.TOUCH", "")]
    [InlineData("HW.TOUCH", "x.1")]
    [InlineData("HW.FAIL", "")]
    [InlineData("HW.FAIL", "1.x")]
    public void MalformedToken_EverySettleCommand_HwInvalidToken(string command, string token)
    {
        var message = ErrorOf(() =>
        {
            if (command == "HW.FAIL") _db.Execute(command, token, "SomeException", "d");
            else _db.Execute(command, token);
        });

        message.Should().StartWith("ERR HW_INVALID_TOKEN");
    }

    [Theory]
    [InlineData("HW.ACK")]
    [InlineData("HW.TOUCH")]
    [InlineData("HW.FAIL")]
    public void WellFormedTokenNotHeld_AnswersZero_NotAnError(string command)
    {
        // Parses, but no claim ever carried it: a late settle is a race the client cannot avoid.
        const string token = "999999.1";
        var reply = command == "HW.FAIL"
            ? _db.Execute(command, token, "SomeException", "d")
            : _db.Execute(command, token);

        ((long)reply).Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // HW_INVALID_ARG — control characters (Requirement 3 AC5: at least group
    // and node positions, all four characters)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    [InlineData("\u007f")]
    public void ControlChar_InGroupPosition_HwInvalidArg(string bad)
    {
        var message = ErrorOf(() => _db.Subscribe("ch", $"a{bad}b", "node-1"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    [InlineData("\u007f")]
    public void ControlChar_InNodePosition_HwInvalidArg(string bad)
    {
        var message = ErrorOf(() => _db.Execute("HW.CLAIM", "SVC", "svc", $"node{bad}1"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
    }

    [Fact]
    public void ControlChar_InIdentifier_NeverCorruptsMirrorKey()
    {
        // A rejected group containing \n must not write two mirror entries.
        // Follow-ups run on a FRESH connection: see NewlineDesyncProbe / 004.1
        // research.md § "Finding 6" — a rejected command carrying a raw newline
        // can desync further custom-command parsing on the same session (an
        // upstream Garnet parser quirk; unreachable by Highway clients, which
        // validate identifiers client-side and never send control characters).
        var error = ErrorOf(() => _db.Subscribe("mirror.ch", "a\nb", "node-1"));
        error.Should().StartWith("ERR HW_INVALID_ARG");

        using var fresh = ConnectionMultiplexer.Connect(_server.ConnectionString);
        var freshDb = fresh.GetDatabase();
        freshDb.Subscribe("mirror.ch", "real-group", "node-1").ToString().Should().Be("OK");

        var count = (int)freshDb.Execute("HW.PUBLISH", "mirror.ch", "msg")!;
        count.Should().Be(1, "no phantom fan-out targets may exist");
    }

    // -------------------------------------------------------------------------
    // HW_INVALID_ARG — identifier length cap
    // -------------------------------------------------------------------------

    [Fact]
    public void OverLengthIdentifier_HwInvalidArg()
    {
        var tooLong = new string('a', 257); // MaxIdentifierBytes default is 256

        var message = ErrorOf(() => _db.Execute("HW.CALL", tooLong, "req-1", "payload"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
    }

    // -------------------------------------------------------------------------
    // HW_PAYLOAD_TOO_LARGE — HW.CALL, HW.REPLY, HW.PUBLISH (cap = 64 here)
    // -------------------------------------------------------------------------

    [Fact]
    public void OversizePayload_HwCall_DetailNamesActualAndLimit()
    {
        var message = ErrorOf(() => _db.Execute("HW.CALL", "svc", "req-1", new string('x', 100)));
        message.Should().Be("ERR HW_PAYLOAD_TOO_LARGE 100 > 64");
    }

    [Fact]
    public void OversizePayload_HwReply_HwPayloadTooLarge()
    {
        var message = ErrorOf(() => _db.Execute("HW.REPLY", "req-1", new string('x', 100)));
        message.Should().StartWith("ERR HW_PAYLOAD_TOO_LARGE");
    }

    [Fact]
    public void OversizePayload_HwPublish_HwPayloadTooLarge()
    {
        var message = ErrorOf(() => _db.Execute("HW.PUBLISH", "ch", new string('x', 100)));
        message.Should().StartWith("ERR HW_PAYLOAD_TOO_LARGE");
    }

    // -------------------------------------------------------------------------
    // Arity errors still come from Garnet itself (Requirement 2 AC5)
    // -------------------------------------------------------------------------

    [Fact]
    public void WrongArity_GarnetRejects_WithItsOwnMessage()
    {
        var message = ErrorOf(() => _db.Execute("HW.CALL", "svc", "req-1")); // missing payload

        message.Should().NotStartWith("ERR HW_",
            "arity is enforced by Garnet before Prepare runs");
        message.Should().Contain("wrong number of arguments");
    }

    // -------------------------------------------------------------------------
    // Separability (Requirement 2 AC4/AC7)
    // -------------------------------------------------------------------------

    [Fact]
    public void NoValidationFailure_EmitsBareTransactionFailed()
    {
        // Requirement 2 AC7, as amended by protocol 5.0: the Garnet-era transient
        // "ERR Transaction failed." left the contract — nothing emits it — so every
        // validation failure carries the HW_ prefix, which is what makes client
        // classification total. The bare string stays asserted as a regression guard.

        // The newline case runs LAST on this connection: a rejected command
        // carrying a raw newline can desync subsequent custom-command parsing
        // on the same session (upstream quirk, see research.md Finding 6).
        var messages = new List<string>
        {
            ErrorOf(() => _db.Execute("HW.CALL", "", "req", "p")),
            ErrorOf(() => _db.Execute("HW.CALL", "svc", "req", new string('x', 100))),
            ErrorOf(() => _db.Subscribe(new string('a', 257), "grp", "node-1")),
            ErrorOf(() => _db.Execute("HW.ACK", "not-a-token")),
            ErrorOf(() => _db.Execute("HW.CLAIM", "SVC", "svc", "n\node")),
        };

        messages.Should().AllSatisfy(m =>
        {
            m.Should().StartWith("ERR HW_");
            m.Should().NotBe("ERR Transaction failed.");
        });
    }

    // -------------------------------------------------------------------------
    // No state mutation on rejection (Requirement 2 AC3)
    // -------------------------------------------------------------------------

    [Fact]
    public void RejectedCall_LeavesQueueUntouched()
    {
        ErrorOf(() => _db.Execute("HW.CALL", "clean.svc", "", "payload"));
        ErrorOf(() => _db.Execute("HW.CALL", "clean.svc", "req", new string('x', 100)));

        _db.ClaimSvc("clean.svc", "node-1").Should().BeNull("rejected commands must not enqueue");
    }

    [Fact]
    public void RejectedPublish_LeavesGroupQueueUntouched()
    {
        _db.Subscribe("clean.ch", "grp", "node-1");

        ErrorOf(() => _db.Execute("HW.PUBLISH", "clean.ch", new string('x', 100)));

        _db.ClaimQ("clean.ch@grp", "node-1").Should().BeNull("rejected publishes must not fan out");
    }

    [Fact]
    public void RejectedReply_LeavesSlotUntouched()
    {
        ErrorOf(() => _db.Execute("HW.REPLY", "clean-req", new string('x', 100)));

        var slot = _db.Result("clean-req");
        slot.HasValue.Should().BeFalse("rejected replies must not write the slot");
    }

    [Fact]
    public void RejectedSubscribe_LeavesGroupUnregistered()
    {
        ErrorOf(() => _db.Subscribe("clean2.ch", "bad\ngroup", "node-1"));

        // Follow-up on a fresh connection (newline-in-arg session quirk, see
        // research.md Finding 6). A valid publish finds zero groups — the
        // rejected group was never registered.
        using var fresh = ConnectionMultiplexer.Connect(_server.ConnectionString);
        var count = (int)fresh.GetDatabase().Execute("HW.PUBLISH", "clean2.ch", "msg")!;
        count.Should().Be(0);
    }
}
