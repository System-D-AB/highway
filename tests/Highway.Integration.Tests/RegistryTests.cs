using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 006 Task 11 — the registry commands driven over real RESP against an embedded
/// server. Since protocol 5.0 (068) registration is <c>HW.SESSION</c>; <c>HW.HEARTBEAT</c>
/// carries only liveness and departure.
/// </summary>
public class RegistryTests : IDisposable
{
    private const string OrdersCatalog =
        """{"services":[{"name":"orders.create","requestType":"R","responseType":"S"}],"channels":[{"name":"orders.placed","subscriberCount":1}]}""";

    private const string EmptyCatalog = """{"services":[],"channels":[]}""";

    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public RegistryTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    private string[] DiscoverNodes(string service)
    {
        var result = (RedisResult[])_db.Execute("HW.DISCOVER", service)!;
        return [.. result.Select(r => (string)((RedisResult[])r!)[0]!)];
    }

    private Dictionary<string, string> Stats(params string[] args)
    {
        var flat = (RedisResult[])_db.Execute("HW.STATS", args)!;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[(string)flat[i]!] = (string)flat[i + 1]!;
        return map;
    }

    // ──────────────────────────────────────────────────────────────────
    // Registration and discovery
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Register_ThenDiscover_ReturnsTheNode()
    {
        ((string)_db.Register("node-1", OrdersCatalog)!).Should().Be("OK");

        DiscoverNodes("orders.create").Should().Equal("node-1");
    }

    [Fact]
    public void Discover_ReturnsNodeIdWithAgeOfLastBeat()
    {
        _db.Register("node-1", OrdersCatalog);

        var pair = (RedisResult[])((RedisResult[])_db.Execute("HW.DISCOVER", "orders.create")!)[0]!;

        pair.Length.Should().Be(2);
        ((string)pair[0]!).Should().Be("node-1");
        long.Parse((string)pair[1]!).Should().BeGreaterThanOrEqualTo(0, "the caller needs freshness, not just identity");
    }

    [Fact]
    public void Discover_UnknownService_ReturnsEmptyArray_NotAnError()
        => DiscoverNodes("nobody.hosts.this").Should().BeEmpty();

    [Fact]
    public void Register_TwoNodesSameService_BothDiscoverable()
    {
        _db.Register("node-1", OrdersCatalog);
        _db.Register("node-2", OrdersCatalog);

        DiscoverNodes("orders.create").Should().BeEquivalentTo(["node-1", "node-2"]);
    }

    [Fact]
    public void Register_EmptyCatalog_IsAValidPureCaller()
    {
        _db.Register("caller-1", EmptyCatalog);

        Stats().Should().ContainKey("nodes").WhoseValue.Should().Be("1");
        DiscoverNodes("orders.create").Should().BeEmpty();
    }

    [Fact]
    public void Register_RepeatedIdenticalCatalog_DoesNotDuplicateState()
    {
        for (var i = 0; i < 5; i++)
            _db.Register("node-1", OrdersCatalog);

        DiscoverNodes("orders.create").Should().ContainSingle("re-registration must be idempotent")
            .Which.Should().Be("node-1");
        Stats()["nodes"].Should().Be("1");
    }

    [Fact]
    public void Register_ChangedCatalog_RemovesServicesNoLongerHosted()
    {
        _db.Register("node-1", OrdersCatalog);
        DiscoverNodes("orders.create").Should().Equal("node-1");

        // Redeployed under the same name, now hosting something else entirely.
        const string newCatalog =
            """{"services":[{"name":"billing.charge","requestType":"R","responseType":"S"}],"channels":[]}""";
        _db.Register("node-1", newCatalog);

        DiscoverNodes("orders.create").Should().BeEmpty("a redeployed node must leave no stale index entries");
        DiscoverNodes("billing.charge").Should().Equal("node-1");
    }

    [Fact]
    public void Session_WithoutCatalog_RegistersNothing()
    {
        // A client with heartbeats off declares a session with no catalog (068): it must not
        // become a registered node, or dead-node recovery would take its work for not beating.
        ((string)_db.Session("quiet-1")!).Should().Be("OK");

        Stats()["nodes"].Should().Be("0");
        ((string)_db.Execute("HW.HEARTBEAT", "quiet-1")!).Should().Be("SESSION");
    }

    // ──────────────────────────────────────────────────────────────────
    // The +SESSION handshake (was +REGISTER) — Requirement 1 AC3/AC5
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Liveness_ForUnregisteredNode_AsksForASession_AndMutatesNothing()
    {
        ((string)_db.Execute("HW.HEARTBEAT", "ghost")!).Should().Be("SESSION");

        Stats()["nodes"].Should().Be("0",
            "a liveness beat must not conjure a registration it has no catalog for");
    }

    [Fact]
    public void Liveness_AfterRegistration_ReturnsOk()
    {
        _db.Register("node-1", OrdersCatalog);

        ((string)_db.Execute("HW.HEARTBEAT", "node-1")!).Should().Be("OK");
    }

    /// <summary>
    /// The reason the handshake exists: pruning removes a node's index entries,
    /// so a liveness beat that merely refreshed a timestamp would leave the node
    /// alive but undiscoverable, with nothing to surface the fault.
    /// </summary>
    [Fact]
    public void Liveness_AfterTheNodeIsGone_AsksForASessionAgain()
    {
        _db.Register("node-1", OrdersCatalog);
        _db.Execute("HW.HEARTBEAT", "node-1", "BYE");

        ((string)_db.Execute("HW.HEARTBEAT", "node-1")!).Should().Be("SESSION");

        // And re-declaring the session restores discoverability — the self-healing loop.
        _db.Register("node-1", OrdersCatalog);
        DiscoverNodes("orders.create").Should().Equal("node-1");
    }

    [Fact]
    public void Liveness_PreservesTheStoredCatalog()
    {
        _db.Register("node-1", OrdersCatalog);

        for (var i = 0; i < 5; i++)
            ((string)_db.Execute("HW.HEARTBEAT", "node-1")!).Should().Be("OK");

        DiscoverNodes("orders.create").Should()
            .ContainSingle("the node stays discoverable across beats without ever resending its catalog")
            .Which.Should().Be("node-1");
    }

    // ──────────────────────────────────────────────────────────────────
    // Departure
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Bye_RemovesTheNodeFromDiscoveryImmediately()
    {
        _db.Register("node-1", OrdersCatalog);

        ((string)_db.Execute("HW.HEARTBEAT", "node-1", "BYE")!).Should().Be("OK");

        DiscoverNodes("orders.create").Should().BeEmpty();
        Stats()["nodes"].Should().Be("0");
    }

    [Fact]
    public void Bye_ForUnknownNode_IsIdempotent()
        => ((string)_db.Execute("HW.HEARTBEAT", "never-existed", "BYE")!).Should().Be("OK");

    [Fact]
    public void Bye_LeavesOtherNodesAlone()
    {
        _db.Register("node-1", OrdersCatalog);
        _db.Register("node-2", OrdersCatalog);

        _db.Execute("HW.HEARTBEAT", "node-1", "BYE");

        DiscoverNodes("orders.create").Should().Equal("node-2");
    }

    // ──────────────────────────────────────────────────────────────────
    // Validation
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Heartbeat_SecondArgOtherThanBye_IsRejected()
    {
        var text = () => _db.Execute("HW.HEARTBEAT", "node-1", "just some text");
        text.Should().Throw<RedisServerException>().WithMessage("ERR HW_INVALID_ARG*");

        // The 4.x registration form (a catalog as the second argument) is gone: registration is HW.SESSION.
        var catalog = () => _db.Execute("HW.HEARTBEAT", "node-1", OrdersCatalog);
        catalog.Should().Throw<RedisServerException>().WithMessage("ERR HW_INVALID_ARG*");
        Stats()["nodes"].Should().Be("0");
    }

    [Fact]
    public void Session_BlankNodeId_IsRejected()
    {
        var act = () => _db.Register("", OrdersCatalog);

        act.Should().Throw<RedisServerException>().WithMessage("ERR HW_INVALID_ARG*");
    }

    [Fact]
    public void Session_NodeIdWithNewline_IsRejected()
    {
        // The registry mirror lists are newline-delimited; an unvalidated
        // identifier would split into two entries and corrupt routing.
        var act = () => _db.Register("node\n2", OrdersCatalog);

        act.Should().Throw<RedisServerException>().WithMessage("ERR HW_INVALID_ARG*");
    }

    [Fact]
    public void Session_OversizedCatalog_IsRejected()
    {
        using var small = new HighwayTestServer(o => o.MaxCatalogBytes = 64);
        using var redis = ConnectionMultiplexer.Connect(small.ConnectionString);

        var big = """{"services":[],"channels":[],"pad":" """ + new string('x', 200) + "\"}";
        var act = () => redis.GetDatabase().Register("node-1", big);

        act.Should().Throw<RedisServerException>().WithMessage("ERR HW_PAYLOAD_TOO_LARGE*");
    }

    [Fact]
    public void Discover_BlankService_IsRejected()
    {
        var act = () => _db.Execute("HW.DISCOVER", "");

        act.Should().Throw<RedisServerException>().WithMessage("ERR HW_INVALID_ARG*");
    }

    // ──────────────────────────────────────────────────────────────────
    // HW.STATS
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Stats_Server_ReportsLiveNodesAndCatalogTotals()
    {
        _db.Register("node-1", OrdersCatalog);
        _db.Register("node-2", OrdersCatalog);

        var stats = Stats();

        stats["kind"].Should().Be("server");
        stats["nodes"].Should().Be("2");
        stats["services"].Should().Be("1", "both nodes host the same service");
        stats["channels"].Should().Be("1");
    }

    [Fact]
    public void Stats_Service_ReportsQueueDepthAndHosts()
    {
        _db.Register("node-1", OrdersCatalog);
        _db.Execute("HW.CALL", "orders.create", "req-1", "payload");
        _db.Execute("HW.CALL", "orders.create", "req-2", "payload");

        var stats = Stats("orders.create");

        stats["kind"].Should().Be("service");
        stats["queueDepth"].Should().Be("2");
        stats["hosts"].Should().Be("1");
        stats["inFlight"].Should().Be("0");
    }

    [Fact]
    public void Stats_Service_CountsClaimedButUnackedAsInFlight()
    {
        _db.Register("node-1", OrdersCatalog);
        _db.Execute("HW.CALL", "orders.create", "req-1", "payload");
        _db.ClaimSvc("orders.create", "node-1").Should().NotBeNull();

        var stats = Stats("orders.create");

        stats["queueDepth"].Should().Be("0");
        stats["inFlight"].Should().Be("1");
    }

    [Fact]
    public void Stats_Channel_ReportsGroupsAndPending()
    {
        _db.Subscribe("orders.placed", "grp-1");
        _db.Execute("HW.PUBLISH", "orders.placed", "m1");
        _db.Execute("HW.PUBLISH", "orders.placed", "m2");

        var stats = Stats("orders.placed");

        stats["kind"].Should().Be("channel");
        stats["groups"].Should().Be("1");
        stats["pending"].Should().Be("2");
    }

    [Fact]
    public void Stats_UnknownName_ReturnsZeroedCounters_NotAnError()
    {
        var stats = Stats("never.seen");

        stats["kind"].Should().Be("channel", "an unregistered name falls through to the channel view");
        stats["groups"].Should().Be("0");
        stats["pending"].Should().Be("0");
    }

    [Fact]
    public void Stats_NameThatIsBothServiceAndChannel_ResolvesAsService()
    {
        const string dual =
            """{"services":[{"name":"dual","requestType":"R","responseType":"S"}],"channels":[{"name":"dual","subscriberCount":1}]}""";
        _db.Register("node-1", dual);
        _db.Subscribe("dual", "grp-1");

        Stats("dual")["kind"].Should().Be("service", "the kind field makes the resolution explicit");
    }

    [Fact]
    public void Stats_ExcludesDepartedNodes()
    {
        _db.Register("node-1", OrdersCatalog);
        _db.Register("node-2", OrdersCatalog);
        _db.Execute("HW.HEARTBEAT", "node-1", "BYE");

        Stats()["nodes"].Should().Be("1");
        Stats("orders.create")["hosts"].Should().Be("1");
    }

    // ──────────────────────────────────────────────────────────────────
    // Durability
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Registrations_SurviveAofRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hw-reg-{Guid.NewGuid():N}");
        try
        {
            using var server = new HighwayTestServer(o => o.DataDir = dir);
            using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
            var db = redis.GetDatabase();

            db.Register("node-1", OrdersCatalog);

            server.Restart();

            using var after = ConnectionMultiplexer.Connect(server.ConnectionString);
            var found = (RedisResult[])after.GetDatabase().Execute("HW.DISCOVER", "orders.create")!;
            found.Length.Should().Be(1, "registrations are durable when AOF is enabled");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
