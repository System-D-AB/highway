using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>039 T3 — Batch 2 producers: HW.CALL, HW.REPLY, HW.PUBLISH.</summary>
public class ProducersTests
{
    // ---------------- HW.CALL ----------------

    [Fact]
    public void Call_EnqueuesToServiceQueue_RepliesOk_Rings()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwCallCommand(), "orders", "req-1", "payload");

        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.ServiceQueue("orders")))
            .Should().Be(1);

        h.Doorbell.Rings.Should().ContainSingle();
        h.Doorbell.Rings[0].Channel.Should().Be(HighwayKeys.ServiceDoorbell("orders"));
    }

    [Fact]
    public void Call_BlankService_InvalidArg_NoWrite()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwCallCommand(), "", "req-1", "payload");
        CommandHarness.AsText(reply).Should().Be("-ERR HW_INVALID_ARG service is blank\r\n");
        h.Doorbell.Rings.Should().BeEmpty();
    }

    // ---------------- HW.REPLY ----------------

    [Fact]
    public void Reply_WritesSlot_ReadableViaGetLive_Rings()
    {
        using var h = new CommandHarness();
        var now = 1_000_000L;

        var reply = h.Run(new HwReplyCommand(), now, "req-1", "the-answer");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        using var snap = h.Store.Snapshot();
        var slot = h.Store.GetLive(snap, HighwayKeyspace.Kv(HighwayNames.ReplySlot("req-1")), now);
        slot.Should().NotBeNull();
        Encoding.UTF8.GetString(slot!).Should().Be("the-answer");

        h.Doorbell.Rings.Should().ContainSingle();
        h.Doorbell.Rings[0].Channel.Should().Be(HighwayKeys.ReplyDoorbell);
    }

    [Fact]
    public void Reply_SlotExpires_AfterTtl()
    {
        var opts = new HighwayServerOptions { ReplySlotTtl = System.TimeSpan.FromMinutes(5) };
        using var h = new CommandHarness(options: opts);
        var now = 1_000_000L;

        h.Run(new HwReplyCommand(), now, "req-1", "answer");

        var afterTtl = now + opts.ReplySlotTtl.Ticks;
        using var snap = h.Store.Snapshot();
        h.Store.GetLive(snap, HighwayKeyspace.Kv(HighwayNames.ReplySlot("req-1")), afterTtl)
            .Should().BeNull("the reply slot must expire once its TTL has passed");
    }

    // ---------------- HW.PUBLISH ----------------

    private static void SeedGroup(IHighwayStore store, string channel, string group)
    {
        using var batch = store.NewBatch();
        store.SetAdd(batch, HighwayKeyspace.SetPrefix(HighwayNames.ChannelGroups(channel)), Encoding.UTF8.GetBytes(group));
        batch.Commit();
    }

    [Fact]
    public void Publish_NoGroups_DeliversToZero()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwPublishCommand(), "news", "hello");
        CommandHarness.AsText(reply).Should().Be(":0\r\n");
    }

    [Fact]
    public void Publish_FansOutToEveryGroup_ReturnsCount()
    {
        using var h = new CommandHarness();
        SeedGroup(h.Store, "news", "billing");
        SeedGroup(h.Store, "news", "shipping");

        var reply = h.Run(new HwPublishCommand(), "news", "hello");
        CommandHarness.AsText(reply).Should().Be(":2\r\n", "delivered to two groups");

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@billing"))).Should().Be(1);
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@shipping"))).Should().Be(1);

        // Rings each group's derived doorbell.
        h.Doorbell.Rings.Select(r => r.Channel).Should().BeEquivalentTo(
            HighwayKeys.QueueDoorbell("news@billing"),
            HighwayKeys.QueueDoorbell("news@shipping"));
    }

    [Fact]
    public void Publish_PerChannelSeq_IsMonotonic()
    {
        using var h = new CommandHarness();
        SeedGroup(h.Store, "news", "g1");

        // Two publishes → message ids 1 then 2. The id is embedded in the queued entry;
        // assert the seq counter advanced (monotonic per channel).
        h.Run(new HwPublishCommand(), "news", "a");
        h.Run(new HwPublishCommand(), "news", "b");

        using var snap = h.Store.Snapshot();
        var seqRaw = h.Store.Get(snap, HighwayKeyspace.Counter(HighwayNames.ChannelSeq("news")));
        seqRaw.Should().NotBeNull();
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(seqRaw!).Should().Be(2);
    }

    [Fact]
    public void Publish_DeferredWithAt_LandsInDelayedSets_ReturnsZero()
    {
        using var h = new CommandHarness();
        SeedGroup(h.Store, "news", "g1");
        var now = 1_000_000L;

        var reply = h.Run(new HwPublishCommand(), now, "news", "hello", "AT", (now + 1_000_000L).ToString());
        CommandHarness.AsText(reply).Should().Be(":0\r\n", "nothing delivered now");

        using var snap = h.Store.Snapshot();
        h.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed("news@g1")))
            .Should().Be(1);
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@g1")))
            .Should().Be(0);
        h.Doorbell.Rings.Should().BeEmpty("deferred publish wakes nobody");
    }

    [Fact]
    public void Publish_OverBudget_RefusesWholeFanout_NoGroupGetsIt()
    {
        var opts = new HighwayServerOptions { MaxQueueBytes = 4 };
        using var h = new CommandHarness(options: opts);
        SeedGroup(h.Store, "news", "g1");
        SeedGroup(h.Store, "news", "g2");

        var reply = h.Run(new HwPublishCommand(), "news", "0123456789");
        CommandHarness.AsText(reply).Should().StartWith("-ERR HW_QUEUE_FULL");

        using var snap = h.Store.Snapshot();
        // All-or-none: neither group received it.
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@g1"))).Should().Be(0);
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@g2"))).Should().Be(0);
        h.Doorbell.Rings.Should().BeEmpty();
    }
}
