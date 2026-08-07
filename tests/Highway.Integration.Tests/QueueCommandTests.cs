using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 014 Phase 1 — the queue commands, driven directly.
///
/// <para>A queue is RPC minus the reply. These tests exercise that at the protocol level,
/// before any client surface exists, so a failure here is unambiguously a server problem.</para>
/// </summary>
public class QueueCommandTests : IDisposable
{
    private const string Queue = "invoices";

    private readonly HighwayTestServer _server = new(o => o.Lease = TimeSpan.FromMilliseconds(50));

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static async Task<(string Id, string Payload)?> ClaimAsync(IDatabase db, string node)
    {
        var r = await db.ExecuteAsync("HW.QCLAIM", Queue, node);
        if (r.IsNull) return null;
        var arr = (RedisResult[])r!;
        return (arr[0].ToString()!, arr[1].ToString()!);
    }

    private static async Task<long> LenAsync(IDatabase db, string key)
        => (long)(await db.ExecuteAsync("LLEN", key));

    [Fact]
    public async Task SendThenClaimThenAck_RoundTrips()
    {
        var db = await ConnectAsync();

        await db.ExecuteAsync("HW.QSEND", Queue, "m-1", "body"u8.ToArray());

        var claimed = await ClaimAsync(db, "worker-a");
        claimed.Should().NotBeNull();
        claimed!.Value.Id.Should().Be("m-1");
        claimed.Value.Payload.Should().Be("body");

        ((long)(await db.ExecuteAsync("HW.QACK", Queue, "worker-a", "m-1"))).Should().Be(1);
        (await LenAsync(db, $"hw:q:{Queue}:proc:worker-a")).Should().Be(0);
    }

    /// <summary>
    /// The capability whose absence made people misuse `PublishAsync`: a send does not need
    /// a running worker.
    /// </summary>
    [Fact]
    public async Task SendWithNoWorkerRunning_IsClaimedWhenOneAppears()
    {
        var db = await ConnectAsync();

        await db.ExecuteAsync("HW.QSEND", Queue, "later-1", "body"u8.ToArray());
        (await LenAsync(db, $"hw:q:{Queue}:q")).Should().Be(1, "the message waits");

        var claimed = await ClaimAsync(db, "worker-arriving-late");
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
            await db.ExecuteAsync("HW.QSEND", Queue, $"share-{i}", "body"u8.ToArray());

        var a = new List<string>();
        var b = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var ca = await ClaimAsync(db, "worker-a");
            if (ca is { } x) { a.Add(x.Id); await db.ExecuteAsync("HW.QACK", Queue, "worker-a", x.Id); }

            var cb = await ClaimAsync(db, "worker-b");
            if (cb is { } y) { b.Add(y.Id); await db.ExecuteAsync("HW.QACK", Queue, "worker-b", y.Id); }
        }

        a.Should().NotBeEmpty();
        b.Should().NotBeEmpty();
        a.Concat(b).Should().OnlyHaveUniqueItems("each message goes to exactly one worker");
        a.Concat(b).Should().HaveCount(6, "every message is claimed exactly once");
    }

    [Fact]
    public async Task EmptyQueue_ReturnsNil()
        => (await ClaimAsync(await ConnectAsync(), "worker-a")).Should().BeNull();

    [Fact]
    public async Task UnacknowledgedMessage_IsRedelivered()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.QSEND", Queue, "abandoned", "body"u8.ToArray());

        (await ClaimAsync(db, "worker-a"))!.Value.Id.Should().Be("abandoned");
        await Task.Delay(120);   // lease expires

        var again = await ClaimAsync(db, "worker-b");
        again.Should().NotBeNull("an unacknowledged message must come back");
        again!.Value.Id.Should().Be("abandoned");
    }

    /// <summary>The dead-letter machinery is inherited from feature 013, not rebuilt.</summary>
    [Fact]
    public async Task PoisonMessage_DeadLetters()
    {
        using var strict = new HighwayTestServer(o =>
        {
            o.Lease = TimeSpan.FromMilliseconds(50);
            o.MaxDeliveryAttempts = 1;
        });

        var db = (await ConnectionMultiplexer.ConnectAsync(strict.ConnectionString)).GetDatabase();
        await db.ExecuteAsync("HW.QSEND", Queue, "poison", "body"u8.ToArray());

        for (var i = 0; i < 4; i++)
        {
            await db.ExecuteAsync("HW.QCLAIM", Queue, "worker-a");
            await Task.Delay(70);
        }
        await db.ExecuteAsync("HW.QCLAIM", Queue, "worker-a");

        ((long)(await db.ExecuteAsync("LLEN", $"hw:q:{Queue}:dlq"))).Should().Be(1);
        ((long)(await db.ExecuteAsync("LLEN", $"hw:q:{Queue}:q"))).Should().Be(0,
            "a dead-lettered message must not still be queued");
    }

    [Fact]
    public async Task DelayedSend_IsNotClaimableEarly()
    {
        var db = await ConnectAsync();
        var at = (DateTime.UtcNow + TimeSpan.FromMilliseconds(600)).Ticks;

        await db.ExecuteAsync("HW.QSEND", Queue, "deferred", "body"u8.ToArray(), "AT", at.ToString());

        (await ClaimAsync(db, "worker-a")).Should().BeNull("the delivery time has not arrived");
        await Task.Delay(800);
        (await ClaimAsync(db, "worker-a"))!.Value.Id.Should().Be("deferred");
    }

    /// <summary>
    /// The reason queues have their own key space: sharing `hw:svc:` would have meant a
    /// silent shared work list.
    /// </summary>
    [Fact]
    public async Task QueueAndServiceMayShareAName()
    {
        var db = await ConnectAsync();

        await db.ExecuteAsync("HW.QSEND", "shared.name", "q-1", "queue-body"u8.ToArray());
        await db.ExecuteAsync("HW.CALL", "shared.name", "r-1", "rpc-body"u8.ToArray());

        var fromQueue = await db.ExecuteAsync("HW.QCLAIM", "shared.name", "worker-a");
        var fromService = await db.ExecuteAsync("HW.DEQUEUE", "shared.name", "worker-a");

        ((RedisResult[])fromQueue!)[0].ToString().Should().Be("q-1");
        ((RedisResult[])fromService!)[0].ToString().Should().Be("r-1");
    }

    [Fact]
    public async Task AckingAnUnknownMessage_ReturnsZero_NotAnError()
        => ((long)(await (await ConnectAsync())
                .ExecuteAsync("HW.QACK", Queue, "worker-a", "never-sent")))
            .Should().Be(0, "a worker retrying an acknowledgement is doing the right thing");

    [Fact]
    public async Task SentMessage_SurvivesABrokerRestart()
    {
        using var durable = new HighwayTestServer(o =>
            o.DataDir = Path.Combine(Path.GetTempPath(), $"hw-q-{Guid.NewGuid():N}"));

        var db = (await ConnectionMultiplexer.ConnectAsync(durable.ConnectionString)).GetDatabase();
        await db.ExecuteAsync("HW.QSEND", Queue, "durable-1", "body"u8.ToArray());

        durable.Restart();

        var after = (await ConnectionMultiplexer.ConnectAsync(durable.ConnectionString)).GetDatabase();
        var claimed = await after.ExecuteAsync("HW.QCLAIM", Queue, "worker-a");

        claimed.IsNull.Should().BeFalse("the queue is the durable store");
        ((RedisResult[])claimed!)[0].ToString().Should().Be("durable-1");
    }
}
