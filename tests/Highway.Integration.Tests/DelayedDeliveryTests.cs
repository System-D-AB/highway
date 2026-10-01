using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 013 Part 2 — delayed delivery.
///
/// <para><b>The guarantee is "not before", not an alarm clock.</b> Promotion is driven by
/// consumer activity rather than a server timer, so a message arrives on the first
/// <c>HW.CLAIM</c> after its delivery time — which in a running system is at most one
/// backstop interval late, and in a channel with no consumer is not at all until one
/// starts. These tests poll explicitly for that reason.</para>
/// </summary>
public class DelayedDeliveryTests : IDisposable
{
    private const string Channel = "delay.ch";
    private const string Group = "grp-1";

    private readonly HighwayTestServer _server = new();

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static long TicksIn(TimeSpan delay) => (DateTime.UtcNow + delay).Ticks;

    private static async Task<int> ReceiveCountAsync(IDatabase db)
    {
        var result = await db.ClaimQAsync($"{Channel}@{Group}", "node-1");
        return result is null ? 0 : 1;
    }

    [Fact]
    public async Task DelayedMessage_IsNotDeliveredEarly_AndIsDeliveredAfter()
    {
        var db = await ConnectAsync();
        await db.SubscribeAsync(Channel, Group);

        // Wall-clock test: the due time leaves seconds, not milliseconds, of slack, because a loaded
        // full-suite run stalls a 200ms delay past a 600ms due time (seen 2026-10-01).
        var due = TimeSpan.FromSeconds(3);
        var published = System.Diagnostics.Stopwatch.StartNew();
        await db.ExecuteAsync("HW.PUBLISH", Channel, "later"u8.ToArray(),
            "AT", TicksIn(due).ToString());

        (await ReceiveCountAsync(db)).Should().Be(0, $"the delivery time has not arrived ({published.ElapsedMilliseconds}ms of {due.TotalMilliseconds}ms)");
        await Task.Delay(200);
        (await ReceiveCountAsync(db)).Should().Be(0, $"still early ({published.ElapsedMilliseconds}ms of {due.TotalMilliseconds}ms)");

        await Task.Delay(due);
        (await ReceiveCountAsync(db)).Should().Be(1, "the delivery time has passed");
    }

    [Fact]
    public async Task PublishWithoutAt_IsUnchanged()
    {
        var db = await ConnectAsync();
        await db.SubscribeAsync(Channel, Group);

        var groups = (long)(await db.ExecuteAsync("HW.PUBLISH", Channel, "now"u8.ToArray()));
        groups.Should().Be(1, "an ordinary publish still fans out immediately");

        (await ReceiveCountAsync(db)).Should().Be(1);
    }

    /// <summary>
    /// A delivery time already in the past is delivered immediately rather than rejected:
    /// clock skew between a client and the broker is normal, and failing a publish over a
    /// few milliseconds of it would be worse than delivering slightly early.
    /// </summary>
    [Fact]
    public async Task DeliveryTimeInThePast_DeliversImmediately()
    {
        var db = await ConnectAsync();
        await db.SubscribeAsync(Channel, Group);

        await db.ExecuteAsync("HW.PUBLISH", Channel, "overdue"u8.ToArray(),
            "AT", TicksIn(TimeSpan.FromMinutes(-5)).ToString());

        (await ReceiveCountAsync(db)).Should().Be(1);
    }

    /// <summary>
    /// After 018, Decision 3: delayed messages are fanned out at **publish time** to each
    /// group's derived queue delayed set. A group that subscribes during the delay does NOT
    /// receive the message. This is the documented semantic change (R5.3).
    /// </summary>
    [Fact]
    public async Task GroupSubscribingDuringTheDelay_DoesNotReceiveTheMessage()
    {
        var db = await ConnectAsync();
        await db.SubscribeAsync(Channel, Group);

        await db.ExecuteAsync("HW.PUBLISH", Channel, "wait"u8.ToArray(),
            "AT", TicksIn(TimeSpan.FromMilliseconds(500)).ToString());

        // Subscribes after the publish, before the delivery time.
        await db.SubscribeAsync(Channel, "late-joiner");

        await Task.Delay(700);
        await ReceiveCountAsync(db);   // any consumer's poll promotes for its own group

        var late = await db.ClaimQAsync($"{Channel}@late-joiner", "node-1");
        late.Should().BeNull(
            "a group registered after publish does not receive a delayed message (018 Decision 3)");
    }

    [Fact]
    public async Task MultipleDelayedMessages_ArriveInDeliveryTimeOrder()
    {
        var db = await ConnectAsync();
        await db.SubscribeAsync(Channel, Group);

        await db.ExecuteAsync("HW.PUBLISH", Channel, "third"u8.ToArray(),
            "AT", TicksIn(TimeSpan.FromMilliseconds(500)).ToString());
        await db.ExecuteAsync("HW.PUBLISH", Channel, "first"u8.ToArray(),
            "AT", TicksIn(TimeSpan.FromMilliseconds(100)).ToString());
        await db.ExecuteAsync("HW.PUBLISH", Channel, "second"u8.ToArray(),
            "AT", TicksIn(TimeSpan.FromMilliseconds(300)).ToString());

        await Task.Delay(800);

        var payloads = new List<string>();
        var derivedQueue = $"{Channel}@{Group}";
        for (var i = 0; i < 3; i++)
        {
            var result = await db.ClaimQAsync(derivedQueue, "node-1");
            if (result is not { } claim) break;
            payloads.Add(claim.PayloadText);
        }

        payloads.Should().Equal(["first", "second", "third"],
            "promotion pops lowest-score-first, so delivery follows delivery time");
    }

    [Fact]
    public async Task DelayedMessage_SurvivesARestart()
    {
        using var durable = new HighwayTestServer(o =>
            o.DataDir = Path.Combine(Path.GetTempPath(), $"hw-delay-{Guid.NewGuid():N}"));

        var db = (await ConnectionMultiplexer.ConnectAsync(durable.ConnectionString)).GetDatabase();
        await db.SubscribeAsync(Channel, Group);
        await db.ExecuteAsync("HW.PUBLISH", Channel, "durable"u8.ToArray(),
            "AT", TicksIn(TimeSpan.FromMilliseconds(700)).ToString());

        durable.Restart();

        var after = (await ConnectionMultiplexer.ConnectAsync(durable.ConnectionString)).GetDatabase();
        await Task.Delay(900);

        var result = await after.ClaimQAsync($"{Channel}@{Group}", "node-1");
        result.Should().NotBeNull("a delayed message must survive a restart");
    }

    [Fact]
    public async Task BadAtArgument_IsRejected()
    {
        var db = await ConnectAsync();

        var notANumber = async () => await db.ExecuteAsync("HW.PUBLISH", Channel, "x"u8.ToArray(), "AT", "soon");
        (await notANumber.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_ARG*tick count*");

        var wrongKeyword = async () => await db.ExecuteAsync("HW.PUBLISH", Channel, "x"u8.ToArray(), "WHEN", "123");
        (await wrongKeyword.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_ARG*expected AT*");
    }
}
