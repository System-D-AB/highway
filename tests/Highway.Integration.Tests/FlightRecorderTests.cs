using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server;
using Highway.Server.Observability;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 002 — the flight recorder over real RESP against an embedded server.
/// </summary>
public class FlightRecorderTests
{
    private const string Catalog =
        """{"services":[{"name":"s","requestType":"R","responseType":"S"}],"channels":[]}""";

    private const string RecorderNamedCatalog =
        """{"services":[{"name":"RECORDER","requestType":"R","responseType":"S"}],"channels":[]}""";

    private static Dictionary<string, string> Fields(RedisResult r)
    {
        var flat = (RedisResult[])r!;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[(string)flat[i]!] = (string)flat[i + 1]!;
        return map;
    }

    private static RedisResult[] Replay(IDatabase db, params string[] args)
        => (RedisResult[])db.Execute("HW.REPLAY", args)!;

    [Fact]
    public void RpcRoundTrip_IsRecordedInOrder_WithCorrectIdentifiers()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "orders.create", "req-1", "the-payload");
        db.Execute("HW.DEQUEUE", "orders.create", "node-1");
        db.Execute("HW.ACK", "orders.create", "node-1", "req-1");

        var events = Replay(db, "orders.create");

        events.Select(e => Fields(e)["eventType"])
            .Should().Equal("RpcEnqueued", "RpcClaimed", "RpcAcknowledged");

        var enqueued = Fields(events[0]);
        enqueued["requestId"].Should().Be("req-1", "request IDs are opaque strings, not GUIDs");
        enqueued["payload"].Should().Be("the-payload");
        enqueued["payloadSize"].Should().Be("11");

        Fields(events[1])["nodeId"].Should().Be("node-1");
    }

    /// <summary>
    /// A flight recorder that showed only successes would omit the thing it
    /// exists for.
    /// </summary>
    [Fact]
    public void RejectedCommand_IsRecorded_WithItsErrorCode()
    {
        using var server = new HighwayTestServer(o => o.MaxPayloadBytes = 8);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        Assert.Throws<RedisServerException>(
            () => db.Execute("HW.CALL", "orders.create", "req-big", new string('x', 100)));

        var events = Replay(db, "orders.create");

        events.Should().ContainSingle("a rejected command is still an event worth seeing");
        Fields(events[0])["errorCode"].Should().Be("HW_PAYLOAD_TOO_LARGE");
    }

    [Fact]
    public void PublishAndReceive_AreRecorded_WithCounts()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.SUBSCRIBE", "orders.placed", "grp-1");
        db.Execute("HW.PUBLISH", "orders.placed", "m1");
        db.Execute("HW.PUBLISH", "orders.placed", "m2");
        db.Execute("HW.RECEIVE", "orders.placed", "grp-1", "COUNT", "10");

        var events = Replay(db, "orders.placed").Select(Fields).ToList();

        events.Select(e => e["eventType"]).Should().Equal(
            "GroupRegistered", "Published", "Published", "MessagesReceived");
        events[1]["count"].Should().Be("1", "one group received the publish");
        events[3]["count"].Should().Be("2", "one event per batch, carrying the batch size");
    }

    [Fact]
    public void LivenessHeartbeats_AreNotRecorded()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "node-1", Catalog);   // registration — recorded
        for (var i = 0; i < 10; i++)
            db.Execute("HW.HEARTBEAT", "node-1");        // liveness — noise

        var events = Replay(db, "node-1").Select(Fields).ToList();

        events.Should().ContainSingle(
            "recording a beat every few seconds per node would evict real history to store that nothing happened");
        events[0]["eventType"].Should().Be("NodeRegistered");
    }

    [Fact]
    public void ReadOnlyCommands_AreNotRecorded()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "orders.create", "r1", "p");
        for (var i = 0; i < 5; i++)
            db.Execute("HW.REPLAY", "orders.create");

        Replay(db, "orders.create").Should().ContainSingle(
            "querying the recorder must not record the query");
    }

    [Fact]
    public void CaptureHeadersOnly_KeepsSizeButNotContent()
    {
        using var server = new HighwayTestServer(o =>
            o.Observability.Overrides["orders.create"] =
                new NameRecorderOptions { Capture = PayloadCapture.HeadersOnly });
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "orders.create", "r1", "sensitive-content");

        var evt = Fields(Replay(db, "orders.create")[0]);
        evt["payload"].Should().BeEmpty("content must not be retained");
        evt["payloadSize"].Should().Be("17", "size stays visible");
    }

    [Fact]
    public void CaptureOff_RecordsNothing_AndLeavesNeighboursAlone()
    {
        using var server = new HighwayTestServer(o =>
            o.Observability.Overrides["noisy.svc"] = new NameRecorderOptions { Capture = PayloadCapture.Off });
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "noisy.svc", "r1", "p");
        db.Execute("HW.CALL", "quiet.svc", "r1", "p");

        Replay(db, "noisy.svc").Should().BeEmpty();
        Replay(db, "quiet.svc").Should().ContainSingle();
    }

    [Fact]
    public void UnknownName_ReturnsEmptyArray_NotAnError()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);

        Replay(redis.GetDatabase(), "never.seen").Should().BeEmpty();
    }

    [Fact]
    public void Replay_HonoursLimitAndWindowAndNode()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "orders.create", "r1", "p");
        db.Execute("HW.DEQUEUE", "orders.create", "node-a");
        db.Execute("HW.CALL", "orders.create", "r2", "p");
        db.Execute("HW.DEQUEUE", "orders.create", "node-b");

        Replay(db, "orders.create", "LIMIT", "2").Should().HaveCount(2);
        Replay(db, "orders.create", "FROM", "-5min").Should().HaveCount(4);
        Replay(db, "orders.create", "FROM", "-1s", "TO", "-1s").Should().BeEmpty();
        Replay(db, "orders.create", "NODE", "node-a").Should().ContainSingle();
    }

    [Theory]
    [InlineData("LIMIT", "0", "ERR HW_INVALID_COUNT")]
    [InlineData("LIMIT", "999999", "ERR HW_INVALID_COUNT")]
    [InlineData("LIMIT", "abc", "ERR HW_INVALID_COUNT")]
    [InlineData("FROM", "nonsense", "ERR HW_INVALID_ARG")]
    [InlineData("WHAT", "x", "ERR HW_INVALID_ARG")]
    public void Replay_InvalidArguments_UseTheEstablishedErrorContract(
        string keyword, string value, string expectedPrefix)
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);

        var act = () => redis.GetDatabase().Execute("HW.REPLAY", "orders.create", keyword, value);

        act.Should().Throw<RedisServerException>().WithMessage($"{expectedPrefix}*");
    }

    [Fact]
    public void ReplayDisabled_RefusesQueriesButKeepsRecording()
    {
        using var server = new HighwayTestServer(o => o.Observability.ReplayEnabled = false);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "orders.create", "r1", "p");

        var act = () => db.Execute("HW.REPLAY", "orders.create");
        act.Should().Throw<RedisServerException>().WithMessage("*disabled*");

        Fields(db.Execute("HW.STATS", "RECORDER"))["events"].Should().Be("1",
            "the recorder keeps running; only the query surface is refused");
    }

    [Fact]
    public void StatsRecorder_ReportsHealth()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "orders.create", "r1", "p");

        var stats = Fields(db.Execute("HW.STATS", "RECORDER"));

        stats["kind"].Should().Be("recorder");
        stats["enabled"].Should().Be("1");
        stats["names"].Should().Be("1");
        int.Parse(stats["events"]).Should().BeGreaterThan(0);
        stats["failures"].Should().Be("0", "a non-zero failure count means a bug worth reporting");
    }

    [Fact]
    public void StatsRecorder_AnswersWhenDisabled()
    {
        using var server = new HighwayTestServer(o => o.Observability.RecorderEnabled = false);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.CALL", "orders.create", "r1", "p");

        var stats = Fields(db.Execute("HW.STATS", "RECORDER"));
        stats["enabled"].Should().Be("0", "reporting the disabled state beats erroring");
        stats["events"].Should().Be("0");
    }

    [Fact]
    public void StatsRecorder_IsReservedAndBeatsAServiceOfThatName()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "node-1", RecorderNamedCatalog);

        Fields(db.Execute("HW.STATS", "recorder"))["kind"].Should().Be("recorder",
            "RECORDER is reserved, matched case-insensitively, and takes priority");
    }

    /// <summary>
    /// The recorder lives in process memory, never in the keyspace — so it must
    /// contribute nothing to the store or the AOF.
    /// </summary>
    [Fact]
    public void Recording_AddsNoKeysToTheStore()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        for (var i = 0; i < 50; i++)
            db.Execute("HW.CALL", "orders.create", $"r{i}", new string('x', 500));

        Replay(db, "orders.create").Should().HaveCount(50, "the events exist in memory");

        var recorderKeys = (RedisResult[])db.Execute("KEYS", "hw:fdr:*")!;
        recorderKeys.Should().BeEmpty("the recorder must not put anything in the Garnet keyspace");
    }
}
