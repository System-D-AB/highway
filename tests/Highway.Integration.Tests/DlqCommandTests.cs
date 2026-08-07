using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 013 T5 — <c>HW.DLQ</c>, the operator surface over dead letters.
///
/// <para>Without this, dead-lettering only half solves the problem: the queue stops
/// looping, but nobody can see what stopped it, replay it after a fix, or clear it.</para>
/// </summary>
public class DlqCommandTests : IDisposable
{
    private const string Service = "dlqcmd.svc";

    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(50);
        o.MaxDeliveryAttempts = 1;
    });

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    /// <summary>Enqueues a request and abandons it until it dead-letters.</summary>
    private static async Task PoisonAsync(IDatabase db, string requestId, string payload = "payload")
    {
        await db.ExecuteAsync("HW.CALL", Service, requestId, System.Text.Encoding.UTF8.GetBytes(payload));
        for (var i = 0; i < 4; i++)
        {
            await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
            await Task.Delay(70);
        }
        await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
    }

    private static async Task<long> ListLengthAsync(IDatabase db, string key)
        => (long)(await db.ExecuteAsync("LLEN", key));

    [Fact]
    public async Task Peek_ReturnsTheEntry_WithEverythingNeededToDiagnoseIt()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "peek-1", "the-original-body");

        var result = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", Service))!;
        result.Should().HaveCount(1);

        var fields = (RedisResult[])result[0]!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            map[fields[i].ToString()!] = fields[i + 1].ToString()!;

        map.Should().ContainKey("deadLetteredAt");
        map["attempts"].Should().Be("2", "the entry exceeded MaxDeliveryAttempts = 1");
        map["reason"].Should().Be("MAX_ATTEMPTS");
        map["requestId"].Should().Be("peek-1");
        map["payload"].Should().Be("the-original-body",
            "an entry that reaches the DLQ stripped of its payload has been thrown away with extra steps");
    }

    [Fact]
    public async Task Peek_IsNonDestructive()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "peek-2");

        await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", Service);
        await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", Service);
        var third = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", Service))!;

        third.Should().HaveCount(1, "looking must not consume — the operator has to see it to decide");
        (await ListLengthAsync(db, $"hw:svc:{Service}:dlq")).Should().Be(1);
    }

    [Fact]
    public async Task Requeue_MovesBackToTheLiveQueue_AndResetsAttempts()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "requeue-1");

        var moved = (long)(await db.ExecuteAsync("HW.DLQ", "REQUEUE", "SVC", Service));
        moved.Should().Be(1);

        (await ListLengthAsync(db, $"hw:svc:{Service}:dlq")).Should().Be(0);
        (await ListLengthAsync(db, $"hw:svc:{Service}:q")).Should().Be(1);

        // The attempt count was reset, so the request is deliverable again rather than
        // dead-lettering on its very next sweep.
        var claimed = await db.ExecuteAsync("HW.DEQUEUE", Service, "node-b");
        claimed.IsNull.Should().BeFalse("a requeued request must be claimable");

        var arr = (RedisResult[])claimed!;
        arr[0].ToString().Should().Be("requeue-1");
    }

    [Fact]
    public async Task Purge_Removes()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "purge-1");

        var removed = (long)(await db.ExecuteAsync("HW.DLQ", "PURGE", "SVC", Service));
        removed.Should().Be(1);
        (await ListLengthAsync(db, $"hw:svc:{Service}:dlq")).Should().Be(0);
        (await ListLengthAsync(db, $"hw:svc:{Service}:q")).Should().Be(0,
            "purge discards, it does not requeue");
    }

    [Fact]
    public async Task UnknownTarget_ReturnsEmpty_NotAnError()
    {
        var db = await ConnectAsync();

        var peeked = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", "never.seen"))!;
        peeked.Should().BeEmpty("an operator querying a quiet name deserves an answer, not a failure");

        ((long)(await db.ExecuteAsync("HW.DLQ", "PURGE", "SVC", "never.seen"))).Should().Be(0);
        ((long)(await db.ExecuteAsync("HW.DLQ", "REQUEUE", "CH", "never.seen", "grp"))).Should().Be(0);
    }

    [Fact]
    public async Task Count_LimitsTheBatch()
    {
        var db = await ConnectAsync();
        for (var i = 0; i < 5; i++)
            await PoisonAsync(db, $"count-{i}");

        var peeked = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", Service, "COUNT", "2"))!;
        peeked.Should().HaveCount(2);

        var purged = (long)(await db.ExecuteAsync("HW.DLQ", "PURGE", "SVC", Service, "COUNT", "3"));
        purged.Should().Be(3);
        (await ListLengthAsync(db, $"hw:svc:{Service}:dlq")).Should().Be(2);
    }

    [Fact]
    public async Task BadArguments_AreRejectedWithNamedCodes()
    {
        var db = await ConnectAsync();

        var badAction = async () => await db.ExecuteAsync("HW.DLQ", "DESTROY", "SVC", Service);
        (await badAction.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_ARG*expected PEEK, REQUEUE or PURGE*");

        var badTarget = async () => await db.ExecuteAsync("HW.DLQ", "PEEK", "QUEUE", Service);
        (await badTarget.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_ARG*expected SVC, Q or CH*");

        var badCount = async () => await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", Service, "COUNT", "0");
        (await badCount.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_COUNT*");
    }

    /// <summary>
    /// Dead-lettering makes a previously loud failure quiet, so a non-zero DLQ has to be
    /// visible where an operator already looks (feature 013 T6).
    /// </summary>
    [Fact]
    public async Task Stats_ReportsDeadLetterDepth()
    {
        var db = await ConnectAsync();

        // HW.STATS resolves a name as a service only when the discovery index knows it,
        // so a host has to have registered. See Stats_OnAServiceWithNoRegisteredHost for
        // the gap this leaves.
        await db.ExecuteAsync("HW.HEARTBEAT", "node-a",
            $$"""{"services":[{"name":"{{Service}}","requestType":"R","responseType":"S"}],"channels":[]}""");

        await PoisonAsync(db, "stats-1");
        await PoisonAsync(db, "stats-2");

        var fields = (RedisResult[])(await db.ExecuteAsync("HW.STATS", Service))!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            map[fields[i].ToString()!] = fields[i + 1].ToString()!;

        map["kind"].Should().Be("service");
        map.Should().ContainKey("deadLettered");
        map["deadLettered"].Should().Be("2");
    }

    /// <summary>
    /// <b>A known gap, asserted so it is a recorded decision rather than a surprise.</b>
    ///
    /// <para><c>HW.STATS</c> resolves a name as a service only when the discovery index
    /// knows it. A service whose hosts have all departed or been pruned therefore reports
    /// as a channel — with a <c>deadLettered</c> count of zero, because the channel form
    /// reads group dead-letter lists rather than the service one.</para>
    ///
    /// <para>That is exactly the state an operator investigating a poison message may be
    /// in: the host crashed, its registration expired, and the dead letters it left behind
    /// become invisible to the command they would most likely reach for. <c>HW.DLQ PEEK</c>
    /// still finds them, because it is told the target kind explicitly.</para>
    ///
    /// <para>Not fixed here: the resolution would have to read the service queue or
    /// dead-letter list, and both are object-store structures whose <c>Prepare</c>-phase
    /// read registers a watch that the command's own locks would then fail (004.1).
    /// Fixing it properly means deciding the kind in <c>Main</c>, which is a change to
    /// <c>HW.STATS</c>'s shape rather than an addition to it.</para>
    /// </summary>
    [Fact]
    public async Task Stats_OnAServiceWithNoRegisteredHost_ReportsAsAChannel()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "orphan-1");

        var fields = (RedisResult[])(await db.ExecuteAsync("HW.STATS", Service))!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            map[fields[i].ToString()!] = fields[i + 1].ToString()!;

        map["kind"].Should().Be("channel", "no host has registered this service");
        map["deadLettered"].Should().Be("0", "the channel form does not read the service dead-letter list");

        // The dead letter is genuinely there, and HW.DLQ finds it.
        var peeked = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", Service))!;
        peeked.Should().HaveCount(1, "HW.DLQ is told the target kind, so it is unaffected");
    }
}
