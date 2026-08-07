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
///   message starts with "ERR HW_"  → permanent failure
///   bare "ERR Transaction failed." → transient (watch conflict) — retry
///   anything else                  → permanent
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
    [InlineData("HW.DEQUEUE", "", "node-1", null)]
    [InlineData("HW.DEQUEUE", "svc", "", null)]
    [InlineData("HW.ACK", "", "node-1", "req-1")]
    [InlineData("HW.ACK", "svc", "", "req-1")]
    [InlineData("HW.ACK", "svc", "node-1", "")]
    [InlineData("HW.SUBSCRIBE", "", "grp", null)]
    [InlineData("HW.SUBSCRIBE", "ch", "", null)]
    [InlineData("HW.UNSUBSCRIBE", "", "grp", null)]
    [InlineData("HW.UNSUBSCRIBE", "ch", "", null)]
    [InlineData("HW.PUBLISH", "", "payload", null)]
    [InlineData("HW.RECEIVE", "", "grp", null)]
    [InlineData("HW.RECEIVE", "ch", "", null)]
    [InlineData("HW.RACK", "", "grp", "1")]
    [InlineData("HW.RACK", "ch", "", "1")]
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
        var message = ErrorOf(() => _db.Execute("HW.SUBSCRIBE", "ch", $"a{bad}b"));
        message.Should().StartWith("ERR HW_INVALID_ARG");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    [InlineData("\u007f")]
    public void ControlChar_InNodePosition_HwInvalidArg(string bad)
    {
        var message = ErrorOf(() => _db.Execute("HW.DEQUEUE", "svc", $"node{bad}1"));
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
        var error = ErrorOf(() => _db.Execute("HW.SUBSCRIBE", "mirror.ch", "a\nb"));
        error.Should().StartWith("ERR HW_INVALID_ARG");

        using var fresh = ConnectionMultiplexer.Connect(_server.ConnectionString);
        var freshDb = fresh.GetDatabase();
        freshDb.Execute("HW.SUBSCRIBE", "mirror.ch", "real-group").ToString().Should().Be("OK");

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
    // HW_INVALID_COUNT — every invalid COUNT variant on HW.RECEIVE
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("abc")]     // non-numeric
    [InlineData("12x")]     // non-numeric
    [InlineData("0")]       // zero
    [InlineData("-5")]      // negative
    [InlineData("99999999999999999999")] // overflow
    [InlineData("501")]     // above ReceiveMaxCount (default 500)
    public void InvalidCount_HwInvalidCount(string count)
    {
        var message = ErrorOf(() => _db.Execute("HW.RECEIVE", "ch", "grp", "COUNT", count));
        message.Should().StartWith("ERR HW_INVALID_COUNT");
    }

    [Fact]
    public void InvalidCount_BareForm_AlsoRejected()
    {
        var message = ErrorOf(() => _db.Execute("HW.RECEIVE", "ch", "grp", "-1"));
        message.Should().StartWith("ERR HW_INVALID_COUNT");
    }

    [Fact]
    public void ValidCount_WithKeyword_StillWorks()
    {
        _db.Execute("HW.SUBSCRIBE", "count.ch", "grp");
        _db.Execute("HW.PUBLISH", "count.ch", "m");

        var result = (RedisResult[])_db.Execute("HW.RECEIVE", "count.ch", "grp", "COUNT", "10")!;
        result.Should().HaveCount(1);
    }

    // -------------------------------------------------------------------------
    // HW.RACK messageId validation
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("99999999999999999999")]
    public void Rack_InvalidMessageId_HwInvalidArg(string messageId)
    {
        var message = ErrorOf(() => _db.Execute("HW.RACK", "ch", "grp", messageId));
        message.Should().StartWith("ERR HW_INVALID_ARG");
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
        // Requirement 2 AC7: ideally a forced watch conflict demonstrates the
        // transient message. Watch conflicts require a concurrent mutation
        // between Prepare's mirror read and lock acquisition, which cannot be
        // forced deterministically from a single connection. We therefore assert
        // the weaker invariant: no Highway validation path ever produces the
        // bare string, which is what makes client classification total.

        // The newline case runs LAST on this connection: a rejected command
        // carrying a raw newline can desync subsequent custom-command parsing
        // on the same session (upstream quirk, see research.md Finding 6).
        var messages = new List<string>
        {
            ErrorOf(() => _db.Execute("HW.CALL", "", "req", "p")),
            ErrorOf(() => _db.Execute("HW.CALL", "svc", "req", new string('x', 100))),
            ErrorOf(() => _db.Execute("HW.RECEIVE", "ch", "grp", "COUNT", "0")),
            ErrorOf(() => _db.Execute("HW.RECEIVE", "ch", "grp", "COUNT", "-1")),
            ErrorOf(() => _db.Execute("HW.RACK", "ch", "grp", "abc")),
            ErrorOf(() => _db.Execute("HW.SUBSCRIBE", new string('a', 257), "grp")),
            ErrorOf(() => _db.Execute("HW.DEQUEUE", "svc", "n\node")),
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

        var result = _db.Execute("HW.DEQUEUE", "clean.svc", "node-1");
        result.IsNull.Should().BeTrue("rejected commands must not enqueue");
    }

    [Fact]
    public void RejectedPublish_LeavesGroupQueueUntouched()
    {
        _db.Execute("HW.SUBSCRIBE", "clean.ch", "grp");

        ErrorOf(() => _db.Execute("HW.PUBLISH", "clean.ch", new string('x', 100)));

        var result = (RedisResult[])_db.Execute("HW.RECEIVE", "clean.ch", "grp", "COUNT", "10")!;
        result.Should().BeEmpty("rejected publishes must not fan out");
    }

    [Fact]
    public void RejectedReply_LeavesSlotUntouched()
    {
        ErrorOf(() => _db.Execute("HW.REPLY", "clean-req", new string('x', 100)));

        var slot = _db.StringGet("hw:rep:clean-req");
        slot.HasValue.Should().BeFalse("rejected replies must not write the slot");
    }

    [Fact]
    public void RejectedSubscribe_LeavesGroupUnregistered()
    {
        ErrorOf(() => _db.Execute("HW.SUBSCRIBE", "clean2.ch", "bad\ngroup"));

        // Follow-up on a fresh connection (newline-in-arg session quirk, see
        // research.md Finding 6). A valid publish finds zero groups — the
        // rejected group was never registered.
        using var fresh = ConnectionMultiplexer.Connect(_server.ConnectionString);
        var count = (int)fresh.GetDatabase().Execute("HW.PUBLISH", "clean2.ch", "msg")!;
        count.Should().Be(0);
    }
}
