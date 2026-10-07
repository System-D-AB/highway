// 074 (D2): RPC has no dead letters — HW.DLQ serves queues and subscriber groups, so these tests drive a
// queue (HW.SEND / HW.CLAIM Q). They were written against a service before 074.
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
    private const string Queue = "dlqcmd.q";

    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(50);
        o.MaxDeliveryAttempts = 1;
    });

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    /// <summary>Sends a message and abandons it until it dead-letters.</summary>
    private static async Task PoisonAsync(IDatabase db, string requestId, string payload = "payload")
    {
        await db.ExecuteAsync("HW.SEND", Queue, requestId, System.Text.Encoding.UTF8.GetBytes(payload));
        for (var i = 0; i < 4; i++)
        {
            await db.ClaimQAsync(Queue, "node-a");
            await Task.Delay(70);
        }
        await db.ClaimQAsync(Queue, "node-a");
    }

    private long ListLength(string key) => _server.Inspect.ListLength(key);

    [Fact]
    public async Task Peek_ReturnsTheEntry_WithEverythingNeededToDiagnoseIt()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "peek-1", "the-original-body");

        var result = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", Queue))!;
        result.Should().HaveCount(1);

        var fields = (RedisResult[])result[0]!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            map[fields[i].ToString()!] = fields[i + 1].ToString()!;

        map.Should().ContainKey("deadLetteredAt");
        map["attempts"].Should().Be("2", "the entry exceeded MaxDeliveryAttempts = 1");
        map["reason"].Should().Be("MAX_ATTEMPTS");
        map["messageId"].Should().Be("peek-1");
        map["payload"].Should().Be("the-original-body",
            "an entry that reaches the DLQ stripped of its payload has been thrown away with extra steps");
    }

    [Fact]
    public async Task Peek_IsNonDestructive()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "peek-2");

        await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", Queue);
        await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", Queue);
        var third = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", Queue))!;

        third.Should().HaveCount(1, "looking must not consume — the operator has to see it to decide");
        (ListLength($"hw:q:{Queue}:dlq")).Should().Be(1);
    }

    [Fact]
    public async Task Requeue_MovesBackToTheLiveQueue_AndResetsAttempts()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "requeue-1");

        var moved = (long)(await db.ExecuteAsync("HW.DLQ", "REQUEUE", "Q", Queue));
        moved.Should().Be(1);

        (ListLength($"hw:q:{Queue}:dlq")).Should().Be(0);
        (ListLength($"hw:q:{Queue}:q")).Should().Be(1);

        // The attempt count was reset, so the request is deliverable again rather than
        // dead-lettering on its very next sweep.
        var claimed = await db.ClaimQAsync(Queue, "node-b");
        claimed.Should().NotBeNull("a requeued request must be claimable");
        claimed!.Value.Id.Should().Be("requeue-1");
    }

    [Fact]
    public async Task Purge_Removes()
    {
        var db = await ConnectAsync();
        await PoisonAsync(db, "purge-1");

        var removed = (long)(await db.ExecuteAsync("HW.DLQ", "PURGE", "Q", Queue));
        removed.Should().Be(1);
        (ListLength($"hw:q:{Queue}:dlq")).Should().Be(0);
        (ListLength($"hw:q:{Queue}:q")).Should().Be(0,
            "purge discards, it does not requeue");
    }

    [Fact]
    public async Task UnknownTarget_ReturnsEmpty_NotAnError()
    {
        var db = await ConnectAsync();

        var peeked = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", "never.seen"))!;
        peeked.Should().BeEmpty("an operator querying a quiet name deserves an answer, not a failure");

        ((long)(await db.ExecuteAsync("HW.DLQ", "PURGE", "Q", "never.seen"))).Should().Be(0);
        ((long)(await db.ExecuteAsync("HW.DLQ", "REQUEUE", "Q", "never.seen@grp"))).Should().Be(0);
    }

    [Fact]
    public async Task Count_LimitsTheBatch()
    {
        var db = await ConnectAsync();
        for (var i = 0; i < 5; i++)
            await PoisonAsync(db, $"count-{i}");

        var peeked = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", Queue, "COUNT", "2"))!;
        peeked.Should().HaveCount(2);

        var purged = (long)(await db.ExecuteAsync("HW.DLQ", "PURGE", "Q", Queue, "COUNT", "3"));
        purged.Should().Be(3);
        (ListLength($"hw:q:{Queue}:dlq")).Should().Be(2);
    }

    [Fact]
    public async Task BadArguments_AreRejectedWithNamedCodes()
    {
        var db = await ConnectAsync();

        var badAction = async () => await db.ExecuteAsync("HW.DLQ", "DESTROY", "Q", Queue);
        (await badAction.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_ARG*expected PEEK, REQUEUE, PURGE, RESOLVE or COUNT*");

        var badTarget = async () => await db.ExecuteAsync("HW.DLQ", "PEEK", "QUEUE", Queue);
        (await badTarget.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_ARG*the accepted form is Q*");

        // 074 (D2): a service target is refused, naming the rule.
        var svcTarget = async () => await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", "dlqcmd.svc");
        (await svcTarget.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*HW_INVALID_ARG*RPC has no dead letters*");

        var badCount = async () => await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", Queue, "COUNT", "0");
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

        await PoisonAsync(db, "stats-1");
        await PoisonAsync(db, "stats-2");

        var fields = (RedisResult[])(await db.ExecuteAsync("HW.STATS", "Q:" + Queue))!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            map[fields[i].ToString()!] = fields[i + 1].ToString()!;

        map["kind"].Should().Be("queue");
        map.Should().ContainKey("deadLettered");
        map["deadLettered"].Should().Be("2");
    }
}
