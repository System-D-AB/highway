using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// Feature 073 (R2): The dead-letter feed — storage and declaration.
/// Tests complete-set semantics for dead-letter feed declarations in HW.SESSION:
/// - Joining sets membership and CFeedOn on the target box.
/// - Dropping a feed removes membership and decrements CFeedOn.
/// - When the last member leaves, CFeedOn is set to 0 and the feed box drains (DrainingSince set).
/// - Re-declaring resumes the feed (DrainingSince cleared, CFeedOn set).
/// - HW.HEARTBEAT BYE PURGE leaves all feeds declared by the departing node cleanly.
/// </summary>
public class DeadLetterFeedDeclarationTests
{
    private static readonly long T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static string DeadLettersJson(params (string Kind, string Name)[] feeds)
        => "{\"deadLetters\":[" + string.Join(",", feeds.Select(f => $"{{\"kind\":\"{f.Kind}\",\"name\":\"{f.Name}\"}}")) + "]}";

    private static void Session(CommandHarness h, long now, string node, params (string Kind, string Name)[] feeds)
        => CommandHarness.AsText(h.Run(new HwSessionCommand(), now, node, DeadLettersJson(feeds))).Should().Be("+OK\r\n");

    private static string Heartbeat(CommandHarness h, long now, string node, string form = "OK")
        => form == "BYE PURGE"
            ? CommandHarness.AsText(h.Run(new HwHeartbeatCommand(), now, node, "BYE", "PURGE"))
            : CommandHarness.AsText(h.Run(new HwHeartbeatCommand(), now, node));

    private static string[] Members(CommandHarness h, string setName)
    {
        using var snap = h.Store.Snapshot();
        return [.. h.Store.SetMembers(snap, HighwayKeyspace.SetPrefix(setName)).Select(m => Encoding.UTF8.GetString(m))];
    }

    private static (long FeedOn, long DrainingSince)? TargetAndFeed(CommandHarness h, BoxKeys.Kind kind, string name)
    {
        var rt = new BoxRuntime();
        using var batch = h.Store.NewBatch();
        var targetBox = Catalog.Find(h.Store, batch, rt, kind, name);
        if (targetBox is null) return null;

        var targetString = BoxCommandSupport.FeedTargetName(kind, name);
        var feedBox = Catalog.Find(h.Store, batch, rt, BoxKeys.Kind.DeadFeed, targetString);

        using var wbTarget = new WorkBox(h.Store, rt, targetBox.Value, tracksBytes: kind == BoxKeys.Kind.Queue, batch);
        long feedDraining = 0;
        if (feedBox is not null)
        {
            using var wbFeed = new WorkBox(h.Store, rt, feedBox.Value, tracksBytes: false, batch);
            feedDraining = wbFeed.DrainingSince;
        }

        return (wbTarget.FeedOn, feedDraining);
    }

    [Fact]
    public void DeclareFeed_CreatesMembership_SetsCFeedOn()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("Q", "payments"), ("Q", "orders.create"), ("Q", "orders@billing"));

        Members(h, HighwayNames.FeedMembers("Q:payments")).Should().BeEquivalentTo("node-1");
        Members(h, HighwayNames.FeedMembers("Q:orders.create")).Should().BeEquivalentTo("node-1");
        Members(h, HighwayNames.FeedMembers("Q:orders@billing")).Should().BeEquivalentTo("node-1");

        Members(h, HighwayNames.NodeFeeds("node-1")).Should().BeEquivalentTo("Q:payments", "Q:orders.create", "Q:orders@billing");

        TargetAndFeed(h, BoxKeys.Kind.Queue, "payments").Should().Be((1L, 0L));
        TargetAndFeed(h, BoxKeys.Kind.Queue, "orders.create").Should().Be((1L, 0L));
        TargetAndFeed(h, BoxKeys.Kind.Queue, "orders@billing").Should().Be((1L, 0L));
    }

    [Fact]
    public void MultipleNodesDeclareFeed_IncrementsCFeedOn()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("Q", "payments"));
        Session(h, T0, "node-2", ("Q", "payments"));

        Members(h, HighwayNames.FeedMembers("Q:payments")).Should().BeEquivalentTo("node-1", "node-2");
        TargetAndFeed(h, BoxKeys.Kind.Queue, "payments")!.Value.FeedOn.Should().Be(2L);
    }

    [Fact]
    public void DroppingFeed_OthersRemain_DecrementsCFeedOn()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("Q", "payments"));
        Session(h, T0, "node-2", ("Q", "payments"));

        // node-1 redeployed without the feed
        Session(h, T0 + 10, "node-1");

        Members(h, HighwayNames.FeedMembers("Q:payments")).Should().BeEquivalentTo("node-2");
        Members(h, HighwayNames.NodeFeeds("node-1")).Should().BeEmpty();
        TargetAndFeed(h, BoxKeys.Kind.Queue, "payments").Should().Be((1L, 0L));
    }

    [Fact]
    public void DroppingLastMember_DrainsFeed_SetsCFeedOnZero()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("Q", "payments"));

        // node-1 drops the feed
        Session(h, T0 + 10, "node-1");

        Members(h, HighwayNames.FeedMembers("Q:payments")).Should().BeEmpty();
        Members(h, HighwayNames.NodeFeeds("node-1")).Should().BeEmpty();

        var state = TargetAndFeed(h, BoxKeys.Kind.Queue, "payments");
        state.Should().NotBeNull();
        state!.Value.FeedOn.Should().Be(0L);
        state.Value.DrainingSince.Should().Be(T0 + 10);
    }

    [Fact]
    public void ReDeclaring_ResumesFeed()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("Q", "payments"));
        Session(h, T0 + 10, "node-1"); // drains

        TargetAndFeed(h, BoxKeys.Kind.Queue, "payments")!.Value.DrainingSince.Should().Be(T0 + 10);

        Session(h, T0 + 20, "node-1", ("Q", "payments")); // resumes

        var state = TargetAndFeed(h, BoxKeys.Kind.Queue, "payments");
        state.Should().Be((1L, 0L), "resumed: CFeedOn is 1 and DrainingSince is 0");
    }

    [Fact]
    public void ByePurge_LeavesAllDeclaredFeeds()
    {
        using var h = new CommandHarness();
        // Register node-1 and node-2 with catalogs so heartbeat touch seen works
        var catalogJson = "{\"services\":[\"s1\"]}";
        h.Run(new HwSessionCommand(), T0, "node-1", "{\"catalog\":" + catalogJson + ",\"deadLetters\":[{\"kind\":\"Q\",\"name\":\"payments\"},{\"kind\":\"Q\",\"name\":\"orders.create\"}]}");
        h.Run(new HwSessionCommand(), T0, "node-2", "{\"catalog\":" + catalogJson + ",\"deadLetters\":[{\"kind\":\"Q\",\"name\":\"payments\"}]}");

        TargetAndFeed(h, BoxKeys.Kind.Queue, "payments")!.Value.FeedOn.Should().Be(2L);
        TargetAndFeed(h, BoxKeys.Kind.Queue, "orders.create")!.Value.FeedOn.Should().Be(1L);

        // node-1 departs via BYE PURGE
        var reply = Heartbeat(h, T0 + 10, "node-1", "BYE PURGE");
        reply.Should().StartWith("*3\r\n:0\r\n:0\r\n:0\r\n");

        Members(h, HighwayNames.NodeFeeds("node-1")).Should().BeEmpty();
        Members(h, HighwayNames.FeedMembers("Q:payments")).Should().BeEquivalentTo("node-2");
        Members(h, HighwayNames.FeedMembers("Q:orders.create")).Should().BeEmpty();

        TargetAndFeed(h, BoxKeys.Kind.Queue, "payments")!.Value.FeedOn.Should().Be(1L, "node-2 remains");
        var svcState = TargetAndFeed(h, BoxKeys.Kind.Queue, "orders.create");
        svcState!.Value.FeedOn.Should().Be(0L, "node-1 was last member");
        svcState.Value.DrainingSince.Should().Be(T0 + 10, "feed is now draining");
    }

    [Theory]
    [InlineData("{\"deadLetters\":\"not an array\"}", "'deadLetters' must be an array")]
    [InlineData("{\"deadLetters\":[\"not an object\"]}", "each deadLetter entry must be an object")]
    [InlineData("{\"deadLetters\":[{\"name\":\"q1\"}]}", "deadLetter: 'kind' is required")]
    [InlineData("{\"deadLetters\":[{\"kind\":\"INVALID\",\"name\":\"q1\"}]}", "deadLetter: kind must be Q")]
    [InlineData("{\"deadLetters\":[{\"kind\":\"SVC\",\"name\":\"orders\"}]}", "RPC has no dead letters")]   // 074 (D2)
    [InlineData("{\"deadLetters\":[{\"kind\":\"Q\"}]}", "deadLetter: 'name' is required")]
    [InlineData("{\"deadLetters\":[{\"kind\":\"Q\",\"name\":\"\"}]}", "deadLetter:")]
    public void InvalidDeadLettersJson_FailsSession(string json, string expectedErrorSubstring)
    {
        using var h = new CommandHarness();
        var reply = CommandHarness.AsText(h.Run(new HwSessionCommand(), T0, "node-1", json));
        reply.Should().StartWith("-ERR");
        reply.Should().Contain(expectedErrorSubstring);
    }
}
