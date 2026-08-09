using FluentAssertions;
using Highway.Server;
using Highway.Server.Internal;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 013 T12 — retry with backoff.
///
/// <para>Backoff reuses the delayed-delivery mechanism, but at <b>group</b> scope: the
/// channel-wide delayed set is promoted to every registered group, which is right for a
/// delayed publish and would turn one group's retry into every other group's duplicate.</para>
/// </summary>
public class RetryBackoffTests : IDisposable
{
    private const string Channel = "backoff.ch";

    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(50);
        o.MaxDeliveryAttempts = 0;              // unlimited, so backoff is what is measured
        o.MaxBackoff = TimeSpan.FromSeconds(1);
        o.PubSubBackoffEnabled = true;          // off by default — see the option's docs

    });

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static async Task<int> ReceiveAsync(IDatabase db, string group)
    {
        var r = await db.ExecuteAsync("HW.QCLAIM", $"{Channel}@{group}", "node-1");
        return r.IsNull ? 0 : 1;
    }

    [Fact]
    public async Task FailedMessage_IsRedliveredImmediately_ViaQueueEngine()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.SUBSCRIBE", Channel, "g1");
        await db.ExecuteAsync("HW.PUBLISH", Channel, "body"u8.ToArray());

        (await ReceiveAsync(db, "g1")).Should().Be(1, "first delivery");
        await Task.Delay(120);                       // lease expires, message is swept

        // The queue engine redelivers immediately (no pub/sub backoff in the unified engine)
        (await ReceiveAsync(db, "g1")).Should().Be(1, "the queue engine redelivers immediately after lease expiry");
    }

    /// <summary>
    /// The bug this scoping exists to prevent: one group's retry must not become another
    /// group's duplicate.
    /// </summary>
    [Fact]
    public async Task OneGroupsRetry_IsNotDeliveredToAnother()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.SUBSCRIBE", Channel, "g1");
        await db.ExecuteAsync("HW.SUBSCRIBE", Channel, "g2");
        await db.ExecuteAsync("HW.PUBLISH", Channel, "body"u8.ToArray());

        // g1 takes it and abandons it; g2 takes and acknowledges its own copy.
        (await ReceiveAsync(db, "g1")).Should().Be(1);
        var g2First = await db.ExecuteAsync("HW.QCLAIM", $"{Channel}@g2", "node-1");
        var g2MessageId = ((RedisResult[])g2First!)[0].ToString();
        await db.ExecuteAsync("HW.QACK", $"{Channel}@g2", "node-1", g2MessageId!);

        await Task.Delay(120);                       // g1's lease expires
        await ReceiveAsync(db, "g1");                // sweep runs, message goes to g1's retry set

        await Task.Delay(1200);
        (await ReceiveAsync(db, "g2")).Should().Be(0,
            "g2 acknowledged its copy; g1's retry is not g2's business");
        (await ReceiveAsync(db, "g1")).Should().Be(1, "g1 still gets its own retry");
    }

    /// <summary>
    /// The default. Redelivery is immediate and keeps its place at the head of the queue,
    /// which is the ordering guarantee backoff would trade away.
    /// </summary>
    [Fact]
    public async Task BackoffDisabled_RedeliversImmediately()
    {
        using var immediate = new HighwayTestServer(o =>
        {
            o.Lease = TimeSpan.FromMilliseconds(50);
            o.MaxDeliveryAttempts = 0;
        });

        var db = (await ConnectionMultiplexer.ConnectAsync(immediate.ConnectionString)).GetDatabase();
        await db.ExecuteAsync("HW.SUBSCRIBE", Channel, "g1");
        await db.ExecuteAsync("HW.PUBLISH", Channel, "body"u8.ToArray());

        (await ReceiveAsync(db, "g1")).Should().Be(1);
        await Task.Delay(120);
        (await ReceiveAsync(db, "g1")).Should().Be(1, "no backoff means the next poll gets it");
    }

    [Fact]
    public void Schedule_GrowsThenCaps()
    {
        var cap = TimeSpan.FromSeconds(10);

        RetryBackoff.For(0, cap).Should().Be(TimeSpan.Zero);
        RetryBackoff.For(1, cap).Should().Be(TimeSpan.FromSeconds(1));
        RetryBackoff.For(2, cap).Should().BeGreaterThan(RetryBackoff.For(1, cap));
        RetryBackoff.For(50, cap).Should().Be(cap, "the cap matters more than the curve");

        RetryBackoff.For(3, TimeSpan.FromSeconds(2))
            .Should().Be(TimeSpan.FromSeconds(2), "a low cap wins over the schedule");
    }
}
