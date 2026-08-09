using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 023 — <b>messages, not protocol events.</b>
///
/// <para>An entity page used to show six rows for two messages: <c>QueueSent</c>,
/// <c>QueueClaimed</c>, <c>QueueAcknowledged</c>, twice. Not one of them a thing the developer
/// did — they wrote <c>SendAsync</c> and a handler ran. These tests are the projection that
/// turns the transport back into the traffic.</para>
///
/// <para>Server-side deliberately: a browser doing this would fetch every event to group them
/// and re-implement what "acknowledged" means. Here it is ordinary code with ordinary tests,
/// which the dashboard's logic has never been.</para>
/// </summary>
public class MessageProjectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 9, 20, 35, 47, TimeSpan.Zero);

    private static HighwayEvent Event(
        HighwayEventType type, string name, string? id = "msg-1",
        int msOffset = 0, string? node = null, string? error = null)
        => new()
        {
            Timestamp = T0.AddMilliseconds(msOffset),
            EventType = type,
            Name = name,
            RequestId = id,
            NodeId = node,
            ErrorCode = error,
        };

    // ---- T1a: the taxonomy ----------------------------------------------------

    /// <summary>
    /// The guard. A new event type cannot be added without someone deciding which side of the
    /// line it falls on — the mechanism 016's <c>BoundedStructureTests</c> used, which caught a
    /// real omission on first contact in 017.
    /// </summary>
    [Fact]
    public void EveryEventType_IsClassified()
    {
        var expected = new Dictionary<HighwayEventType, EventVisibility>
        {
            // Public: the developer's code caused it, or must act on it.
            [HighwayEventType.RpcEnqueued] = EventVisibility.Public,
            [HighwayEventType.RpcReplied] = EventVisibility.Public,
            [HighwayEventType.Published] = EventVisibility.Public,
            [HighwayEventType.QueueSent] = EventVisibility.Public,
            [HighwayEventType.DeliveryFailed] = EventVisibility.Public,
            [HighwayEventType.RpcDeadLettered] = EventVisibility.Public,
            [HighwayEventType.QueueDeadLettered] = EventVisibility.Public,
            [HighwayEventType.MessageDeadLettered] = EventVisibility.Public,
            [HighwayEventType.SendRefused] = EventVisibility.Public,

            // Internal: the broker recognising its own work.
            [HighwayEventType.RpcClaimed] = EventVisibility.Internal,
            [HighwayEventType.RpcAcknowledged] = EventVisibility.Internal,
            [HighwayEventType.QueueClaimed] = EventVisibility.Internal,
            [HighwayEventType.QueueAcknowledged] = EventVisibility.Internal,
            [HighwayEventType.MessagesReceived] = EventVisibility.Internal,
            [HighwayEventType.MessageAcknowledged] = EventVisibility.Internal,
            [HighwayEventType.GroupRegistered] = EventVisibility.Internal,
            [HighwayEventType.GroupRemoved] = EventVisibility.Internal,
            [HighwayEventType.NodeRegistered] = EventVisibility.Internal,
            [HighwayEventType.NodeDeparted] = EventVisibility.Internal,
            [HighwayEventType.GroupRetired] = EventVisibility.Internal,
            [HighwayEventType.NodeSuspect] = EventVisibility.Internal,
            [HighwayEventType.LeaseRenewed] = EventVisibility.Internal,
            [HighwayEventType.ProcessingCapExceeded] = EventVisibility.Internal,
        };

        var all = Enum.GetValues<HighwayEventType>();

        expected.Should().HaveCount(all.Length,
            "every event type must be classified. If this fails because you added one: decide " +
            "whether it is something the developer's code caused (Public) or the broker moving " +
            "work around (Internal), and add it here. That is the point of the test.");

        foreach (var (type, visibility) in expected)
            EventVisibilityMap.Of(type).Should().Be(visibility, $"{type} is classified {visibility}");
    }

    // ---- T1: correlation ------------------------------------------------------

    [Fact]
    public void ThreeProtocolEvents_BecomeOneMessage()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "invoices.generate", node: "shop-1"),
            Event(HighwayEventType.QueueClaimed, "invoices.generate", msOffset: 8, node: "order-service-1"),
            Event(HighwayEventType.QueueAcknowledged, "invoices.generate", msOffset: 47, node: "order-service-1"),
        };

        var messages = MessageProjection.Summarise("invoices.generate", events);

        messages.Should().HaveCount(1, "six rows for two messages is the defect this fixes");
        messages[0].Outcome.Should().Be(MessageOutcome.Processed);
        messages[0].DurationMs.Should().Be(47);
    }

    /// <summary>
    /// The row exists to say "shop-1 sent it, order-service-1 processed it". A single node
    /// column would have to pick one end, and would pick the wrong one half the time.
    /// </summary>
    [Fact]
    public void AMessageCarriesBothNodes_WhereItStartedAndWhereItFinished()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "q", node: "shop-1"),
            Event(HighwayEventType.QueueClaimed, "q", msOffset: 8, node: "order-service-1"),
            Event(HighwayEventType.QueueAcknowledged, "q", msOffset: 47, node: "order-service-1"),
        };

        var m = MessageProjection.Summarise("q", events).Single();

        m.StartedOnNode.Should().Be("shop-1");
        m.CompletedOnNode.Should().Be("order-service-1");
    }

    /// <summary>
    /// The start is the first <b>public</b> event — what the developer's code did. A claim is
    /// the broker starting work, which is a different moment.
    /// </summary>
    [Fact]
    public void TheStart_IsTheDevelopersAction_NotTheBrokersClaim()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "q", msOffset: 0, node: "shop-1"),
            Event(HighwayEventType.QueueClaimed, "q", msOffset: 500, node: "worker-1"),
            Event(HighwayEventType.QueueAcknowledged, "q", msOffset: 530, node: "worker-1"),
        };

        var m = MessageProjection.Summarise("q", events).Single();

        m.StartedOnNode.Should().Be("shop-1");
        m.DurationMs.Should().Be(530,
            "the developer's clock starts when they sent it, not when a worker got round to it");
    }

    [Fact]
    public void APublishedMessage_CorrelatesByItsChannelSequence()
    {
        // Published records messageId as a long; the group's delivery records the same value as
        // a string. Normalised on read rather than changing what is recorded.
        var events = new[]
        {
            new HighwayEvent
            {
                Timestamp = T0, EventType = HighwayEventType.Published,
                Name = "orders.placed", MessageId = 42, NodeId = "shop-1",
            },
            Event(HighwayEventType.QueueAcknowledged, "orders.placed@billing",
                  id: "42", msOffset: 30, node: "billing-1"),
        };

        var m = MessageProjection.Summarise("orders.placed", events).Single();

        m.Id.Should().Be("42");
        m.Outcome.Should().Be(MessageOutcome.Processed);
        m.StartedOnNode.Should().Be("shop-1");
        m.CompletedOnNode.Should().Be("billing-1");
    }

    // ---- outcomes -------------------------------------------------------------

    [Fact]
    public void AHandlerThatThrew_IsFailed_NotProcessed()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "q", node: "shop-1"),
            Event(HighwayEventType.DeliveryFailed, "q", msOffset: 40, node: "w1",
                  error: "System.InvalidOperationException"),
        };

        var m = MessageProjection.Summarise("q", events).Single();

        m.Outcome.Should().Be(MessageOutcome.Failed);
        m.FailureDetail.Should().Contain("InvalidOperationException");
    }

    /// <summary>
    /// A message can fail one attempt and succeed on a later one. Order decides, not presence —
    /// otherwise every retried message would read as failed for ever.
    /// </summary>
    [Fact]
    public void AFailureFollowedByASuccess_IsProcessed()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "q", node: "shop-1"),
            Event(HighwayEventType.DeliveryFailed, "q", msOffset: 40, node: "w1", error: "TimeoutException"),
            Event(HighwayEventType.QueueAcknowledged, "q", msOffset: 900, node: "w2"),
        };

        MessageProjection.Summarise("q", events).Single()
            .Outcome.Should().Be(MessageOutcome.Processed);
    }

    [Fact]
    public void ASuccessFollowedByAFailure_IsFailed()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "q", node: "shop-1"),
            Event(HighwayEventType.QueueAcknowledged, "q", msOffset: 40, node: "w1"),
            Event(HighwayEventType.DeliveryFailed, "q", msOffset: 900, node: "w2", error: "TimeoutException"),
        };

        MessageProjection.Summarise("q", events).Single()
            .Outcome.Should().Be(MessageOutcome.Failed);
    }

    [Fact]
    public void DeadLettering_OutranksEverything()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "q", node: "shop-1"),
            Event(HighwayEventType.DeliveryFailed, "q", msOffset: 40, error: "TimeoutException"),
            Event(HighwayEventType.QueueDeadLettered, "q", msOffset: 5000),
        };

        MessageProjection.Summarise("q", events).Single()
            .Outcome.Should().Be(MessageOutcome.DeadLettered);
    }

    [Fact]
    public void ASentButUnfinishedMessage_IsInFlight()
    {
        var events = new[] { Event(HighwayEventType.QueueSent, "q", node: "shop-1") };

        var m = MessageProjection.Summarise("q", events).Single();

        m.Outcome.Should().Be(MessageOutcome.InFlight);
        m.CompletedAt.Should().BeNull("nothing finished, so no completion may be implied");
    }

    // ---- T2: Incomplete -------------------------------------------------------

    /// <summary>
    /// T2 — the recorder is bounded and volatile (002), so under load a message's beginning ages
    /// out while its end remains. Reporting that as anything definite would be a confident lie
    /// at exactly the moment the view matters most.
    /// </summary>
    [Fact]
    public void AMessageWhoseStartHasAgedOut_IsIncomplete_NotInFlight()
    {
        // Only the broker's own steps survive: the public event that began it is gone.
        var events = new[]
        {
            Event(HighwayEventType.QueueClaimed, "q", msOffset: 8, node: "w1"),
        };

        var m = MessageProjection.Summarise("q", events).Single();

        m.Outcome.Should().Be(MessageOutcome.Incomplete);
        m.StartedAt.Should().BeNull();
    }

    // ---- the timeline ---------------------------------------------------------

    [Fact]
    public void TheTimelineKeepsEveryStep_AndLabelsEachOne()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "q", node: "shop-1"),
            Event(HighwayEventType.QueueClaimed, "q", msOffset: 8, node: "w1"),
            Event(HighwayEventType.QueueAcknowledged, "q", msOffset: 47, node: "w1"),
        };

        var steps = MessageProjection.Timeline(events);

        steps.Should().HaveCount(3, "the whole lifecycle is available; the view decides what leads");
        steps[0].Visibility.Should().Be(EventVisibility.Public);
        steps[1].Visibility.Should().Be(EventVisibility.Internal);

        steps[0].SincePreviousMs.Should().BeNull();
        steps[1].SincePreviousMs.Should().Be(8,
            "\"waited 8ms in the queue\" is the diagnosis; two clock times are its raw material");
        steps[2].SincePreviousMs.Should().Be(39);
    }

    // ---- T3: counts -----------------------------------------------------------

    [Fact]
    public void CountsSeparateTheFourWaysAMessageCanEnd()
    {
        var events = new List<HighwayEvent>
        {
            Event(HighwayEventType.QueueSent, "q", id: "ok"),
            Event(HighwayEventType.QueueAcknowledged, "q", id: "ok", msOffset: 10),

            Event(HighwayEventType.QueueSent, "q", id: "bad"),
            Event(HighwayEventType.DeliveryFailed, "q", id: "bad", msOffset: 10, error: "Boom"),

            Event(HighwayEventType.QueueSent, "q", id: "dead"),
            Event(HighwayEventType.QueueDeadLettered, "q", id: "dead", msOffset: 10),

            Event(HighwayEventType.SendRefused, "q", id: "full", error: "HW_QUEUE_FULL"),
        };

        var counts = MessageProjection.Count("q", events);

        // Different problems with different fixes: a handler threw (015), attempts exhausted
        // (013), a byte limit refused it (016). Collapsing them into "errors" hides the fix.
        counts.Processed.Should().Be(1);
        counts.Failed.Should().Be(1);
        counts.DeadLettered.Should().Be(1);
        counts.Refused.Should().Be(1);
    }

    [Fact]
    public void CountsCarryTheirWindow()
    {
        var events = new[] { Event(HighwayEventType.QueueSent, "q") };

        MessageProjection.Count("q", events).WindowStart.Should().Be(T0,
            "\"1,204 processed\" reads as a lifetime total; the window is what makes it actionable");
    }

    // ---- a publish finishes under a DIFFERENT recorder name -------------------

    /// <summary>
    /// The defect this covers was found by looking at the dashboard: every message on a channel
    /// page read <c>InFlight</c> for ever while the samples had processed all of them.
    ///
    /// <para>A channel records only <c>Published</c>. The delivery and the acknowledgement are
    /// recorded under the subscriber group, <c>{channel}@{node}</c>, so a projection given only
    /// the channel's own events sees a message that starts and never ends. Nothing was wrong
    /// with the outcome logic; it was being asked about half a message.</para>
    /// </summary>
    [Fact]
    public void ChannelMessage_WithGroupEvents_Completes()
    {
        var events = new[]
        {
            Event(HighwayEventType.Published, "inventory.low", "7"),
            Event(HighwayEventType.QueueClaimed, "inventory.low@node-a", "7", 15, "node-a"),
            Event(HighwayEventType.QueueAcknowledged, "inventory.low@node-a", "7", 16, "node-a"),
        };

        var only = MessageProjection.Summarise("inventory.low", [events[0]]).Single();
        only.Outcome.Should().Be(MessageOutcome.InFlight, "the channel alone records no ending");

        var joined = MessageProjection.Summarise("inventory.low", events, subscriberGroups: 1).Single();

        joined.Outcome.Should().Be(MessageOutcome.Processed);
        joined.CompletedOnNode.Should().Be("node-a");
        joined.DurationMs.Should().BeApproximately(16, 0.001);
        joined.DeliveredGroups.Should().Be(1);
        joined.SubscriberGroups.Should().Be(1);
    }

    /// <summary>
    /// A fan-out has more than one ending, and a single outcome word has room for one. Two of
    /// three subscribers succeeding is not "processed" and not "failed", and reporting either
    /// hides the half that matters — so the count is carried beside the outcome.
    /// </summary>
    [Fact]
    public void PartialFanout_ReportsGroupsSeparatelyFromTheOutcome()
    {
        var events = new[]
        {
            Event(HighwayEventType.Published, "orders.placed", "9"),
            Event(HighwayEventType.QueueClaimed, "orders.placed@a", "9", 5, "a"),
            Event(HighwayEventType.QueueAcknowledged, "orders.placed@a", "9", 6, "a"),
            Event(HighwayEventType.QueueClaimed, "orders.placed@b", "9", 5, "b"),
            Event(HighwayEventType.QueueAcknowledged, "orders.placed@b", "9", 7, "b"),
            Event(HighwayEventType.QueueClaimed, "orders.placed@c", "9", 5, "c"),
            Event(HighwayEventType.DeliveryFailed, "orders.placed@c", "9", 8, "c", "System.TimeoutException"),
        };

        var m = MessageProjection.Summarise("orders.placed", events, subscriberGroups: 3).Single();

        // Each group is resolved on its OWN events. Resolving the union would let one group's
        // acknowledgement answer for another group's failure.
        m.DeliveredGroups.Should().Be(2);
        m.SubscriberGroups.Should().Be(3);

        // And the outcome still surfaces the failure rather than being averaged away.
        m.Outcome.Should().Be(MessageOutcome.Failed);
        m.FailureDetail.Should().Be("System.TimeoutException");
    }

    /// <summary>
    /// A group that has received nothing leaves no trace in the events, and is exactly the group
    /// worth noticing — a subscriber that is registered and getting none of the traffic. The
    /// count therefore comes from the caller, not from the events.
    /// </summary>
    [Fact]
    public void SilentGroup_IsCountedAsSubscribedButNotDelivered()
    {
        var events = new[]
        {
            Event(HighwayEventType.Published, "inventory.low", "3"),
            Event(HighwayEventType.QueueClaimed, "inventory.low@a", "3", 4, "a"),
            Event(HighwayEventType.QueueAcknowledged, "inventory.low@a", "3", 5, "a"),
        };

        var m = MessageProjection.Summarise("inventory.low", events, subscriberGroups: 2).Single();

        m.DeliveredGroups.Should().Be(1);
        m.SubscriberGroups.Should().Be(2);
    }

    /// <summary>An entity that does not fan out reports no fan-out, rather than "1/1".</summary>
    [Fact]
    public void NonChannel_ReportsNoFanout()
    {
        var events = new[]
        {
            Event(HighwayEventType.QueueSent, "invoices.generate", "4"),
            Event(HighwayEventType.QueueAcknowledged, "invoices.generate", "4", 3, "node-a"),
        };

        var m = MessageProjection.Summarise("invoices.generate", events).Single();

        m.DeliveredGroups.Should().BeNull();
        m.SubscriberGroups.Should().BeNull();
    }
}
