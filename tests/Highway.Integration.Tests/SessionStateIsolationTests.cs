using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Regression guard for per-session procedure reuse (found by feature 002).
///
/// <para>Garnet caches <b>one procedure instance per session</b>
/// (<c>CustomCommandManagerSession.sessionTransactionProcMap</c>) and reuses it
/// for every invocation of that command on that connection. Any instance field
/// not reset therefore leaks into the next call.</para>
///
/// <para>This was not hypothetical: a single validation failure left the captured
/// error set, and every subsequent invocation of that command on the same
/// connection replayed it — a perfectly valid request answering with the previous
/// request's rejection. Every test in the suite missed it because they each used a
/// fresh connection, or never issued a good call after a bad one.</para>
///
/// <para><c>HighwayCommandBase.Prepare</c> is now sealed and resets state before
/// delegating to <c>PrepareCore</c>, which makes the class of bug structurally
/// impossible rather than fixed once. These tests keep it that way.</para>
/// </summary>
public class SessionStateIsolationTests
{
    [Fact]
    public void ValidationFailure_DoesNotPoisonTheNextCallOnTheSameConnection()
    {
        using var server = new HighwayTestServer(o => o.MaxPayloadBytes = 16);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        Assert.Throws<RedisServerException>(
            () => db.Execute("HW.CALL", "svc", "r1", new string('x', 100)));

        var result = (string)db.Execute("HW.CALL", "svc", "r2", "ok")!;

        result.Should().Be("OK",
            "a previous rejection must not be replayed for a valid command");
    }

    [Fact]
    public void RepeatedFailuresThenSuccess_AllBehaveIndependently()
    {
        using var server = new HighwayTestServer(o => o.MaxPayloadBytes = 16);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        for (var i = 0; i < 5; i++)
        {
            Assert.Throws<RedisServerException>(
                () => db.Execute("HW.CALL", "svc", $"bad{i}", new string('x', 100)));
            ((string)db.Execute("HW.CALL", "svc", $"good{i}", "ok")!).Should().Be("OK");
        }
    }

    [Fact]
    public void BlankIdentifierRejection_DoesNotPoisonOtherCommands()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        Assert.Throws<RedisServerException>(() => db.Execute("HW.SUBSCRIBE", "", "grp"));

        ((string)db.Execute("HW.SUBSCRIBE", "ch", "grp")!).Should().Be("OK");
        ((long)db.Execute("HW.PUBLISH", "ch", "m")!).Should().Be(1);
    }

    /// <summary>
    /// The recorder made this visible: a claimed request ID left over from a
    /// previous successful dequeue caused every subsequent NIL dequeue to
    /// re-record a claim that never happened.
    /// </summary>
    [Fact]
    public void NilDequeueAfterASuccessfulOne_RecordsNothingExtra()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "svc", "r1", "p");
        db.Execute("HW.DEQUEUE", "svc", "node-1");
        db.Execute("HW.DEQUEUE", "svc", "node-1").IsNull.Should().BeTrue();
        db.Execute("HW.DEQUEUE", "svc", "node-1").IsNull.Should().BeTrue();

        var claims = ((RedisResult[])db.Execute("HW.REPLAY", "svc")!)
            .Select(e =>
            {
                var flat = (RedisResult[])e!;
                for (var i = 0; i + 1 < flat.Length; i += 2)
                    if ((string)flat[i]! == "eventType") return (string)flat[i + 1]!;
                return "?";
            })
            .Count(t => t == "RpcClaimed");

        claims.Should().Be(1, "one request was claimed once; a nil dequeue claims nothing");
    }

    [Fact]
    public void EmptyReceiveAfterANonEmptyOne_RecordsNothingExtra()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.SUBSCRIBE", "ch", "grp");
        db.Execute("HW.PUBLISH", "ch", "m1");
        db.Execute("HW.RECEIVE", "ch", "grp", "COUNT", "10");
        db.Execute("HW.RECEIVE", "ch", "grp", "COUNT", "10");
        db.Execute("HW.RECEIVE", "ch", "grp", "COUNT", "10");

        var received = ((RedisResult[])db.Execute("HW.REPLAY", "ch")!)
            .Select(e =>
            {
                var flat = (RedisResult[])e!;
                for (var i = 0; i + 1 < flat.Length; i += 2)
                    if ((string)flat[i]! == "eventType") return (string)flat[i + 1]!;
                return "?";
            })
            .Count(t => t == "MessagesReceived");

        received.Should().Be(1, "only the batch that actually returned messages is an event");
    }
}
