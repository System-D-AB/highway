using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Observability;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 022 T1/T2 — <b>every entity says what it is, and the server decides.</b>
///
/// <para>The dashboard's main page listed ten rows in one column called "Name" containing six
/// different kinds of thing: nodes, services, a queue, channels, group queues and an internal
/// bucket. Nothing distinguished them, because the dashboard rendered the flight recorder's name
/// index — a faithful view of the recorder and a useless view of the broker.</para>
/// </summary>
public class CatalogueTests : IDisposable
{
    private readonly HighwayTestServer _server = new();

    public void Dispose() => _server.Dispose();

    private IDatabase Db() => ConnectionMultiplexer.Connect(_server.ConnectionString).GetDatabase();

    private static byte[] Envelope()
        => Encoding.UTF8.GetBytes("""{"v":1,"src":"t","ts":"2026-08-09T00:00:00Z","body":{}}""");

    private static void Register(IDatabase db, string node, string catalogJson)
        => db.Execute("HW.HEARTBEAT", node, catalogJson);

    /// <summary>Reproduces the samples' shape — the deployment the original screenshot came from.</summary>
    private void SeedSamplesShape(IDatabase db)
    {
        Register(db, "order-service-1",
            """{"nodeId":"order-service-1","services":[{"name":"orders.create"},{"name":"orders.get"}],"queues":[{"name":"invoices.generate"}],"channels":[{"name":"inventory.low"}]}""");

        Register(db, "shop-1",
            """{"nodeId":"shop-1","services":[],"queues":[],"channels":[{"name":"orders.placed"}]}""");

        db.Execute("HW.SUBSCRIBE", "orders.placed", "shop-1");
        db.Execute("HW.SUBSCRIBE", "inventory.low", "order-service-1");
        db.Execute("HW.PUBLISH", "orders.placed", Envelope());
        db.Execute("HW.QSEND", "invoices.generate", "inv-1", Envelope());
    }

    // ---- classification (T1) --------------------------------------------------

    [Fact]
    public async Task EveryEntityCarriesAKind_DecidedByTheServer()
    {
        var db = Db();
        SeedSamplesShape(db);

        var catalogue = await _server.ReadCatalogueAsync();
        var byName = catalogue.ToDictionary(e => e.Name, e => e.Kind);

        byName["orders.create"].Should().Be(EntityKind.Service);
        byName["invoices.generate"].Should().Be(EntityKind.Queue);
        byName["orders.placed"].Should().Be(EntityKind.Channel);
        byName["orders.placed@shop-1"].Should().Be(EntityKind.Group);

        byName.Should().NotContainKey("__unclassified__",
            "and nothing is classified by parsing a string in a browser - '@' is a separator the " +
            "SERVER derives (018 T0), so only the server can be sure what it means");
    }

    [Fact]
    public async Task AGroupNamesItsParentChannel_SoTheViewCanNestIt()
    {
        var db = Db();
        SeedSamplesShape(db);

        var catalogue = await _server.ReadCatalogueAsync();
        var group = catalogue.Single(e => e.Name == "orders.placed@shop-1");

        group.ParentChannel.Should().Be("orders.placed",
            "listing a channel and its group as peers is the single biggest reason the old page " +
            "was unreadable, and nesting is a rendering decision the server has to enable");
    }

    [Fact]
    public async Task InternalNamesAreMarkedInternal_NotShownAsUserEntities()
    {
        var db = Db();

        // An RPC round trip creates the reserved reply bucket feature 015 introduced.
        db.Execute("HW.CALL", "cat.svc", "req-1", Envelope());
        db.Execute("HW.REPLY", "req-1", Envelope());

        var catalogue = await _server.ReadCatalogueAsync();
        var replies = catalogue.SingleOrDefault(e => e.Name == "hw.replies");

        replies.Should().NotBeNull("it is in the recorder, so the catalogue sees it");
        replies!.Kind.Should().Be(EntityKind.Internal,
            "hw.replies exists because 015 stopped RPC replies creating one buffer per request - " +
            "showing it to an operator displays a fix for a bug they never had");
    }

    [Fact]
    public async Task NodeIdsAreClassifiedAsNodes_NotAsAnonymousNames()
    {
        var db = Db();
        SeedSamplesShape(db);

        var catalogue = await _server.ReadCatalogueAsync();

        catalogue.Should().Contain(e => e.Name == "shop-1" && e.Kind == EntityKind.Node,
            "HW.HEARTBEAT records under the node id, so every node becomes a recorder name - " +
            "that is a symptom, and once nodes are first-class they must not also appear as " +
            "unlabelled rows");
    }

    // ---- the union (T2) -------------------------------------------------------

    /// <summary>
    /// The case the old dashboard could not distinguish from a healthy one, and the reason the
    /// catalogue is a union rather than just the registry.
    /// </summary>
    [Fact]
    public async Task AnEntityAddressedButNeverDeclared_IsVisibleAndSaysSo()
    {
        var db = Db();
        SeedSamplesShape(db);

        // Called by someone, hosted by nobody — a service that was never deployed.
        db.Execute("HW.CALL", "payments.refund", "req-1", Envelope());

        var catalogue = await _server.ReadCatalogueAsync();
        var orphan = catalogue.SingleOrDefault(e => e.Name == "payments.refund");

        orphan.Should().NotBeNull(
            "the registry alone would never show it - only the recorder knows traffic addressed it");
        orphan!.State.Should().Be(EntityState.NeverDeclared,
            "a service nobody serves is a real failure that looks identical to a healthy one " +
            "when all you have is a depth number");
    }

    [Fact]
    public async Task ADeclaredAndLiveEntity_IsLive()
    {
        var db = Db();
        SeedSamplesShape(db);

        var catalogue = await _server.ReadCatalogueAsync();
        var service = catalogue.Single(e => e.Name == "orders.create");

        service.State.Should().Be(EntityState.Live);
        service.Hosts.Should().Contain("order-service-1",
            "navigation has to work both ways - from an entity to its hosts, and back");
    }

    [Fact]
    public async Task ADeclaredEntityWhoseHostWentQuiet_IsHostStale_NotNeverDeclared()
    {
        using var quick = new HighwayTestServer(o => o.NodeExpiry = TimeSpan.FromMilliseconds(200));
        var db = ConnectionMultiplexer.Connect(quick.ConnectionString).GetDatabase();

        Register(db, "fading-node",
            """{"nodeId":"fading-node","services":[{"name":"fading.svc"}],"queues":[],"channels":[]}""");

        await Task.Delay(400);   // the node stops heartbeating

        var catalogue = await quick.ReadCatalogueAsync();
        var service = catalogue.Single(e => e.Name == "fading.svc");

        // This distinction is the whole reason HasLiveHost became an enum: "declared by a node
        // that went quiet" and "never declared by anyone" need different actions.
        service.State.Should().Be(EntityState.HostStale);
    }

    // ---- nodes (T4's data) ----------------------------------------------------

    [Fact]
    public async Task NodesReportWhatTheyDeclared()
    {
        var db = Db();
        SeedSamplesShape(db);

        var nodes = await _server.ReadNodesAsync();
        var order = nodes.Single(n => n.Name == "order-service-1");

        order.Services.Should().BeEquivalentTo(["orders.create", "orders.get"]);
        order.Queues.Should().BeEquivalentTo(["invoices.generate"]);
        order.Channels.Should().BeEquivalentTo(["inventory.low"]);
        order.IsLive.Should().BeTrue();
    }

    [Fact]
    public async Task ANodeThatDeclaredNothing_IsStillListed()
    {
        var db = Db();
        Register(db, "empty-node", """{"nodeId":"empty-node","services":[],"queues":[],"channels":[]}""");

        var nodes = await _server.ReadNodesAsync();

        nodes.Should().Contain(n => n.Name == "empty-node",
            "an empty catalog is usually a misconfiguration, and it is invisible today");
    }

    /// <summary>
    /// The premise of the first version of this test was wrong, and finding that out was worth
    /// more than the test: <b>the server refuses to register an unparseable catalog</b>, so one
    /// can never reach storage through the normal path. <c>Catalogue.ReadNode</c>'s tolerance of
    /// bad JSON is defence against a corrupted record, not a case an operator can cause.
    /// </summary>
    [Fact]
    public void AnUnreadableCatalog_IsRefusedAtRegistration_SoItNeverReachesTheCatalogue()
    {
        var db = Db();

        var act = () => Register(db, "broken-node", "not json at all");

        act.Should().Throw<RedisServerException>().WithMessage("*parseable catalog JSON*",
            "rejecting it at the door beats storing something no reader can trust");
    }
}
