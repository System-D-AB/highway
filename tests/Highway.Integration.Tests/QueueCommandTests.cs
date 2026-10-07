// 074: these tests make raw HW.CALLs to services no session declared, so fail-fast is off for their servers.
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 014 Phase 1 — the queue commands, driven directly (protocol 5.0: <c>HW.SEND</c>,
/// <c>HW.CLAIM Q</c>, <c>HW.ACK token</c>).
///
/// <para>A queue is RPC minus the reply. These tests exercise that at the protocol level,
/// before any client surface exists, so a failure here is unambiguously a server problem.</para>
/// </summary>
public class QueueCommandTests : IDisposable
{
    private const string Queue = "invoices";

    private readonly HighwayTestServer _server = new(o => { o.Lease = TimeSpan.FromMilliseconds(50); o.RpcFailFast = false; });

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private long Len(string key) => _server.Inspect.ListLength(key);

    [Fact]
    public async Task SendThenClaimThenAck_RoundTrips()
    {
        var db = await ConnectAsync();

        await db.SendAsync(Queue, "m-1", "body"u8.ToArray());

        var claimed = await db.ClaimQAsync(Queue, "worker-a");
        claimed.Should().NotBeNull();
        claimed!.Value.Id.Should().Be("m-1");
        claimed.Value.PayloadText.Should().Be("body");

        (await db.AckAsync(claimed.Value.Token)).Should().Be(1);
        (Len($"hw:q:{Queue}:proc:worker-a")).Should().Be(0);
    }

    /// <summary>
    /// The capability whose absence made people misuse `PublishAsync`: a send does not need
    /// a running worker.
    /// </summary>
    [Fact]
    public async Task SendWithNoWorkerRunning_IsClaimedWhenOneAppears()
    {
        var db = await ConnectAsync();

        await db.SendAsync(Queue, "later-1", "body"u8.ToArray());
        (Len($"hw:q:{Queue}:q")).Should().Be(1, "the message waits");

        var claimed = await db.ClaimQAsync(Queue, "worker-arriving-late");
        claimed!.Value.Id.Should().Be("later-1");
    }

    /// <summary>
    /// The property Pub/Sub cannot express: instances share the work rather than each
    /// receiving a copy.
    /// </summary>
    [Fact]
    public async Task MultipleWorkers_ShareTheWork()
    {
        var db = await ConnectAsync();

        for (var i = 0; i < 6; i++)
            await db.SendAsync(Queue, $"share-{i}", "body"u8.ToArray());

        var a = new List<string>();
        var b = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var ca = await db.ClaimQAsync(Queue, "worker-a");
            if (ca is { } x) { a.Add(x.Id); await db.AckAsync(x.Token); }

            var cb = await db.ClaimQAsync(Queue, "worker-b");
            if (cb is { } y) { b.Add(y.Id); await db.AckAsync(y.Token); }
        }

        a.Should().NotBeEmpty();
        b.Should().NotBeEmpty();
        a.Concat(b).Should().OnlyHaveUniqueItems("each message goes to exactly one worker");
        a.Concat(b).Should().HaveCount(6, "every message is claimed exactly once");
    }

    /// <summary>Nothing due is an empty array in 5.0 (was nil), so a claim loop never special-cases null.</summary>
    [Fact]
    public async Task EmptyQueue_ReturnsEmptyArray()
    {
        var reply = await (await ConnectAsync()).ExecuteAsync("HW.CLAIM", "Q", Queue, "worker-a");

        reply.IsNull.Should().BeFalse("nothing due is an empty array, not nil");
        ((RedisResult[])reply!).Should().BeEmpty();
    }

    [Fact]
    public async Task UnacknowledgedMessage_IsRedelivered()
    {
        var db = await ConnectAsync();
        await db.SendAsync(Queue, "abandoned", "body"u8.ToArray());

        (await db.ClaimQAsync(Queue, "worker-a"))!.Value.Id.Should().Be("abandoned");
        await Task.Delay(120);   // lease expires

        var again = await db.ClaimQAsync(Queue, "worker-b");
        again.Should().NotBeNull("an unacknowledged message must come back");
        again!.Value.Id.Should().Be("abandoned");
    }

    /// <summary>The dead-letter machinery is inherited from feature 013, not rebuilt.</summary>
    [Fact]
    public async Task PoisonMessage_DeadLetters()
    {
        using var strict = new HighwayTestServer(o =>
        {
            o.RpcFailFast = false;
            o.Lease = TimeSpan.FromMilliseconds(50);
            o.MaxDeliveryAttempts = 1;
        });

        var db = (await ConnectionMultiplexer.ConnectAsync(strict.ConnectionString)).GetDatabase();
        await db.SendAsync(Queue, "poison", "body"u8.ToArray());

        for (var i = 0; i < 4; i++)
        {
            await db.ClaimQAsync(Queue, "worker-a");
            await Task.Delay(70);
        }
        await db.ClaimQAsync(Queue, "worker-a");

        (strict.Inspect.ListLength($"hw:q:{Queue}:dlq")).Should().Be(1);
        (strict.Inspect.ListLength($"hw:q:{Queue}:q")).Should().Be(0,
            "a dead-lettered message must not still be queued");
    }

    [Fact]
    public async Task DelayedSend_IsNotClaimableEarly()
    {
        var db = await ConnectAsync();
        var at = (DateTime.UtcNow + TimeSpan.FromMilliseconds(600)).Ticks;

        await db.SendAsync(Queue, "deferred", "body"u8.ToArray(), at);

        (await db.ClaimQAsync(Queue, "worker-a")).Should().BeNull("the delivery time has not arrived");
        await Task.Delay(800);
        (await db.ClaimQAsync(Queue, "worker-a"))!.Value.Id.Should().Be("deferred");
    }

    /// <summary>
    /// The reason queues have their own key space: sharing `hw:svc:` would have meant a
    /// silent shared work list. In 5.0 each is its own box, told apart by <c>SVC</c> / <c>Q</c>.
    /// </summary>
    [Fact]
    public async Task QueueAndServiceMayShareAName()
    {
        var db = await ConnectAsync();

        await db.SendAsync("shared.name", "q-1", "queue-body"u8.ToArray());
        await db.ExecuteAsync("HW.CALL", "shared.name", "r-1", "rpc-body"u8.ToArray());

        var fromQueue = await db.ClaimQAsync("shared.name", "worker-a");
        var fromService = await db.ClaimSvcAsync("shared.name", "worker-a");

        fromQueue!.Value.Id.Should().Be("q-1");
        fromService!.Value.Id.Should().Be("r-1");
    }

    /// <summary>A token that parses but is not held answers <c>:0</c>, never an error.</summary>
    [Fact]
    public async Task AckingAnUnheldToken_ReturnsZero_NotAnError()
    {
        var db = await ConnectAsync();
        await db.SendAsync(Queue, "once", "body"u8.ToArray());
        var claimed = (await db.ClaimQAsync(Queue, "worker-a"))!.Value;

        (await db.AckAsync(claimed.Token)).Should().Be(1);
        (await db.AckAsync(claimed.Token)).Should().Be(0,
            "a worker retrying an acknowledgement is doing the right thing");
        (await db.AckAsync("4000000.1")).Should().Be(0,
            "a well-formed token naming nothing is simply not held");
    }

    [Fact]
    public async Task SentMessage_SurvivesABrokerRestart()
    {
        using var durable = new HighwayTestServer(o =>
            o.DataDir = Path.Combine(Path.GetTempPath(), $"hw-q-{Guid.NewGuid():N}"));

        var db = (await ConnectionMultiplexer.ConnectAsync(durable.ConnectionString)).GetDatabase();
        await db.SendAsync(Queue, "durable-1", "body"u8.ToArray());

        durable.Restart();

        var after = (await ConnectionMultiplexer.ConnectAsync(durable.ConnectionString)).GetDatabase();
        var claimed = await after.ClaimQAsync(Queue, "worker-a");

        claimed.Should().NotBeNull("the queue is the durable store");
        claimed!.Value.Id.Should().Be("durable-1");
    }
}
