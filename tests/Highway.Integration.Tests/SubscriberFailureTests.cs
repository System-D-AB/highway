using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- contracts -------------------------------------------------------------

[Channel("sub.fail")]
public sealed record AlwaysFailsEvent : IPublish
{
    public string Order { get; init; } = "";
}

/// <summary>Throws every time, with a message specific enough to find in a dead letter.</summary>
public sealed class FailingSubscriber : ISubscribe<AlwaysFailsEvent>
{
    public static int Invocations;

    public Task SubscribeAsync(AlwaysFailsEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Invocations);
        throw new InvalidOperationException($"order {message.Order} is already shipped");
    }
}

[Channel("sub.siblings")]
public sealed record SiblingEvent : IPublish
{
    public string Order { get; init; } = "";
}

/// <summary>Succeeds. Counts its runs, so a redelivery's re-run is observable.</summary>
public sealed class GoodSibling : ISubscribe<SiblingEvent>
{
    public static int Invocations;

    public Task SubscribeAsync(SiblingEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Invocations);
        return Task.CompletedTask;
    }
}

/// <summary>Fails, so the delivery fails and its sibling is re-run on redelivery.</summary>
public sealed class BadSibling : ISubscribe<SiblingEvent>
{
    public static int Invocations;

    public Task SubscribeAsync(SiblingEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Invocations);
        throw new TimeoutException("the downstream call timed out");
    }
}

[Channel("sub.idem")]
[Idempotent]
public sealed record IdempotentEvent : IPublish
{
    public string Order { get; init; } = "";
}

/// <summary>Records every dispatch, so suppressed duplicates are visible as an absence.</summary>
public sealed class IdempotentSubscriber : ISubscribe<IdempotentEvent>
{
    public static int Invocations;

    public Task SubscribeAsync(IdempotentEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Invocations);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Feature 018 T2a / R5.4 — <b>subscriber failure adopts queue semantics</b>.
///
/// <para>These drive a <b>real throwing handler through a real node</b>. That matters: the
/// first version of this coverage issued <c>HW.FAIL</c> directly over RESP and never invoked a
/// handler at all, so it passed while the executor was still swallowing every subscriber
/// exception and acknowledging the message. A green test named for behaviour that does not
/// exist is worse than no test — it is the reason the gap survived a review.</para>
///
/// <para>Before 018 every assertion here was false: a throwing subscriber was swallowed, the
/// message acked, and the event gone with no dead letter and nothing in any log. The publisher
/// can do nothing about a subscriber's failure — different process, different machine — which
/// is precisely why the subscriber's own group queue has to keep the evidence.</para>
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class SubscriberFailureTests : IDisposable
{
    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(200);
        o.MaxDeliveryAttempts = 1;
    });

    public SubscriberFailureTests()
    {
        FailingSubscriber.Invocations = 0;
        GoodSibling.Invocations = 0;
        BadSibling.Invocations = 0;
        IdempotentSubscriber.Invocations = 0;
    }

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
    }

    private static Dictionary<string, string> Fields(RedisResult entry)
    {
        var flat = (RedisResult[])entry!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[flat[i].ToString()!] = flat[i + 1].ToString()!;
        return map;
    }

    /// <summary>
    /// The headline: a subscriber that throws must produce a dead letter naming the exception,
    /// not an acknowledged message and silence.
    /// </summary>
    [Fact]
    public async Task SubscriberFailure_DeadLettersWithContext()
    {
        var db = await ConnectAsync();

        await using (var node = await EngineNode.StartAsync(_server.ConnectionString, "sub-fail-node"))
        {
            await node.Client.PublishAsync(new AlwaysFailsEvent { Order = "order-77" });

            // The handler throws on every attempt; attempts exhaust and the sweep dead-letters.
            await WaitForAsync(() =>
                (long)db.Execute("LLEN", "hw:q:sub.fail@sub-fail-node:dlq") > 0);
        }

        FailingSubscriber.Invocations.Should().BeGreaterThan(0,
            "the handler must actually have run - this test exists because its predecessor never invoked one");

        var peeked = (RedisResult[])db.Execute("HW.DLQ", "PEEK", "Q", "sub.fail@sub-fail-node")!;
        peeked.Should().HaveCount(1, "a subscriber that throws every time must dead-letter");

        var dead = Fields(peeked[0]);

        dead.Should().ContainKey("failureType").WhoseValue
            .Should().Be("System.InvalidOperationException",
                "the dead letter must name what threw, or an operator is back to correlating logs");
        dead["failureDetail"].Should().Contain("order order-77 is already shipped");
        dead.Should().NotContainKey("failure",
            "'not reported' would mean the failure target named the wrong queue");
    }

    /// <summary>
    /// R5.4's sharpest edge, asserted rather than described: a sibling that succeeded runs
    /// again when the delivery is redelivered. This test <i>is</i> the documentation of that
    /// trade.
    /// </summary>
    [Fact]
    public async Task SiblingHandlers_ReRunOnRedelivery()
    {
        var db = await ConnectAsync();

        await using (var node = await EngineNode.StartAsync(_server.ConnectionString, "sib-node"))
        {
            await node.Client.PublishAsync(new SiblingEvent { Order = "order-9" });

            // Wait for a second attempt: the failing sibling forces redelivery, and the
            // succeeding one is dragged along with it.
            await WaitForAsync(() => Volatile.Read(ref GoodSibling.Invocations) >= 2);
        }

        BadSibling.Invocations.Should().BeGreaterThan(0, "the failing sibling must have run");

        GoodSibling.Invocations.Should().BeGreaterThan(1,
            "a redelivery re-runs siblings that already succeeded - at-least-once already " +
            "demands idempotent handlers, and [Idempotent] is the remedy for those that cannot be");
    }

    /// <summary>
    /// <c>[Idempotent]</c> was silently ignored for subscribers before 018 — the batch loop had
    /// no dedup gate at all. This proves the gate now runs for a subscriber, keyed on the
    /// derived queue name.
    ///
    /// <para><b>Asserting on the invocation count alone would be vacuous</b>: a successful
    /// handler acknowledges, so nothing redelivers and the count is 1 whether or not the gate
    /// exists. What distinguishes the two worlds is the <b>marker in the store</b> — so that is
    /// what this asserts.</para>
    /// </summary>
    [Fact]
    public async Task IdempotentSubscriber_SuppressesRedeliveredDispatch()
    {
        var db = await ConnectAsync();

        await using (var node = await EngineNode.StartAsync(_server.ConnectionString, "idem-node"))
        {
            await node.Client.PublishAsync(new IdempotentEvent { Order = "order-1" });
            await WaitForAsync(() => Volatile.Read(ref IdempotentSubscriber.Invocations) >= 1);
            await Task.Delay(600);   // long enough for a redelivery, if one were coming
        }

        IdempotentSubscriber.Invocations.Should().Be(1, "a successful subscriber acknowledges");

        // The gate is keyed on the DERIVED QUEUE name, which is what makes it work per group
        // rather than per channel. Before 018 no such key was ever written for a subscriber.
        var markers = ((RedisResult[])db.Execute("KEYS", "hw:idem:sub.idem@idem-node:*")!)
            .Select(k => k.ToString())
            .ToArray();

        markers.Should().NotBeEmpty(
            "[Idempotent] must actually run for a subscriber - before 018 the attribute was " +
            "silently ignored on ISubscribe<T> and no marker was ever written");
    }
}
