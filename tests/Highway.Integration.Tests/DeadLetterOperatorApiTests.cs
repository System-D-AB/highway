using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- Test contracts for dead-letter operator API ----

[Queue("it.dead.op.queue")]
public sealed record OpQueueMessage : ISend
{
    public string Id { get; init; } = "";
    public bool ShouldFail { get; init; } = true;
}

public sealed class OpQueueProcessor : IProcess<OpQueueMessage>
{
    public static readonly ConcurrentBag<string> Processed = new();
    public static bool AllowSuccess = false;

    public Task ProcessAsync(OpQueueMessage message, CancellationToken ct = default)
    {
        if (message.ShouldFail && !AllowSuccess)
            throw new InvalidOperationException($"Intentional processor failure for {message.Id}");

        Processed.Add(message.Id);
        return Task.CompletedTask;
    }
}

[Channel("it.dead.op.channel")]
public sealed record OpChannelMessage : IPublish
{
    public string Id { get; init; } = "";
    public bool ShouldFail { get; init; } = true;
}

public sealed class OpChannelSubscriber : ISubscribe<OpChannelMessage>
{
    public static readonly ConcurrentBag<string> Received = new();
    public static bool AllowSuccess = false;

    public Task SubscribeAsync(OpChannelMessage message, CancellationToken ct = default)
    {
        if (message.ShouldFail && !AllowSuccess)
            throw new InvalidOperationException($"Intentional subscriber failure for {message.Id}");

        Received.Add(message.Id);
        return Task.CompletedTask;
    }
}

[Collection(SubscriberRecorderCollection.Name)]
public class DeadLetterOperatorApiTests : IDisposable
{
    private readonly HighwayTestServer _server;

    public DeadLetterOperatorApiTests()
    {
        OpQueueProcessor.Processed.Clear();
        OpQueueProcessor.AllowSuccess = false;
        OpChannelSubscriber.Received.Clear();
        OpChannelSubscriber.AllowSuccess = false;

        _server = new HighwayTestServer(o =>
        {
            o.Lease = TimeSpan.FromMilliseconds(50);
            o.MaxDeliveryAttempts = 2;
        });
    }

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false)) return;
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task QueueTarget_Peek_Count_PointRequeue_PointPurge()
    {
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-op-1",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await node.Client.SendAsync(new OpQueueMessage { Id = "msg-1", ShouldFail = true });
        await node.Client.SendAsync(new OpQueueMessage { Id = "msg-2", ShouldFail = true });

        // Wait for both to exhaust 2 attempts and move to DLQ
        await WaitForAsync(async () => await node.DeadLetters.CountAsync<OpQueueMessage>() == 2, timeoutMs: 8000);

        var count = await node.DeadLetters.CountAsync<OpQueueMessage>();
        count.Should().Be(2);

        // PeekAsync
        var letters = await node.DeadLetters.PeekAsync<OpQueueMessage>(count: 10);
        letters.Should().HaveCount(2);

        var first = letters[0];
        first.Target.Kind.Should().Be(DeadLetterTargetKind.Queue);
        first.Target.Name.Should().Be("it.dead.op.queue");
        first.Message.Should().NotBeNull();
        first.Message!.Id.Should().Be("msg-1");
        first.Attempts.Should().Be(3);
        first.Requeues.Should().Be(0);
        first.LastFailure.Should().NotBeNull();
        first.LastFailure!.Type.Should().Contain("InvalidOperationException");
        first.DeadLetterId.Should().NotBeNullOrWhiteSpace();

        var second = letters[1];
        second.Message.Should().NotBeNull();
        second.Message!.Id.Should().Be("msg-2");

        // Point Requeue first message
        var requeued = await node.DeadLetters.RequeueAsync<OpQueueMessage>(first.DeadLetterId);
        requeued.Should().BeTrue();

        count = await node.DeadLetters.CountAsync<OpQueueMessage>();
        count.Should().Be(1);

        // Second requeue of same dead letter should return false
        var requeuedAgain = await node.DeadLetters.RequeueAsync<OpQueueMessage>(first.DeadLetterId);
        requeuedAgain.Should().BeFalse();

        // Point Purge second message
        var purged = await node.DeadLetters.PurgeAsync<OpQueueMessage>(second.DeadLetterId);
        purged.Should().BeTrue();

        count = await node.DeadLetters.CountAsync<OpQueueMessage>();
        count.Should().Be(0);

        // Second purge of same dead letter should return false
        var purgedAgain = await node.DeadLetters.PurgeAsync<OpQueueMessage>(second.DeadLetterId);
        purgedAgain.Should().BeFalse();
    }

    [Fact]
    public async Task QueueTarget_RequeueAll_And_PurgeAll()
    {
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-op-batch",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        for (var i = 1; i <= 4; i++)
        {
            await node.Client.SendAsync(new OpQueueMessage { Id = $"batch-{i}", ShouldFail = true });
        }

        await WaitForAsync(async () => await node.DeadLetters.CountAsync<OpQueueMessage>() == 4, timeoutMs: 8000);

        (await node.DeadLetters.CountAsync<OpQueueMessage>()).Should().Be(4);

        // RequeueAll 2 messages
        var requeuedCount = await node.DeadLetters.RequeueAllAsync<OpQueueMessage>(2);
        requeuedCount.Should().Be(2);

        (await node.DeadLetters.CountAsync<OpQueueMessage>()).Should().Be(2);

        // PurgeAll remaining
        var purgedCount = await node.DeadLetters.PurgeAllAsync<OpQueueMessage>(10);
        purgedCount.Should().Be(2);

        (await node.DeadLetters.CountAsync<OpQueueMessage>()).Should().Be(0);
    }

    [Fact]
    public async Task Requeue_TracksLineage()
    {
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-op-lineage",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await node.Client.SendAsync(new OpQueueMessage { Id = "lineage-msg", ShouldFail = true });

        await WaitForAsync(async () => await node.DeadLetters.CountAsync<OpQueueMessage>() == 1, timeoutMs: 8000);

        var letters = await node.DeadLetters.PeekAsync<OpQueueMessage>();
        letters.Should().ContainSingle();
        letters[0].Requeues.Should().Be(0);

        // Requeue via operator API
        var ok = await node.DeadLetters.RequeueAsync<OpQueueMessage>(letters[0].DeadLetterId);
        ok.Should().BeTrue();

        // It re-enters the live queue, gets claimed, fails 2 more attempts, and dead-letters again
        await WaitForAsync(async () => await node.DeadLetters.CountAsync<OpQueueMessage>() == 1, timeoutMs: 8000);

        var secondDeadLetters = await node.DeadLetters.PeekAsync<OpQueueMessage>();
        secondDeadLetters.Should().ContainSingle();
        secondDeadLetters[0].Message.Should().NotBeNull();
        secondDeadLetters[0].Message!.Id.Should().Be("lineage-msg");
        secondDeadLetters[0].Requeues.Should().Be(1);
    }

    [Fact]
    public async Task ChannelGroupTarget_Peek_And_Count()
    {
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-op-channel",
            o =>
            {
                o.SubscriptionGroup = "sub-grp-1";
                o.BackstopInterval = TimeSpan.FromMilliseconds(50);
            });

        await node.Client.PublishAsync(new OpChannelMessage { Id = "chan-1", ShouldFail = true });

        // Wait for it to dead-letter in sub-grp-1
        await WaitForAsync(async () => await node.DeadLetters.CountAsync<OpChannelMessage>() == 1, timeoutMs: 8000);

        var count = await node.DeadLetters.CountAsync<OpChannelMessage>();
        count.Should().Be(1);

        var letters = await node.DeadLetters.PeekAsync<OpChannelMessage>();
        letters.Should().ContainSingle();
        letters[0].Target.Kind.Should().Be(DeadLetterTargetKind.Group);
        letters[0].Target.Name.Should().Be("it.dead.op.channel");
        letters[0].Target.Group.Should().Be("sub-grp-1");
        letters[0].Message.Should().NotBeNull();
        letters[0].Message!.Id.Should().Be("chan-1");
    }

    [Fact]
    public async Task PeekAsync_PoisonPayload_ReturnsNullMessage_AndPreservesRawPayload()
    {
        var db = await ConnectAsync();

        // Enqueue an entry with corrupted/non-JSON payload directly via HW.QSEND
        var poisonBytes = "NOT_A_VALID_HIGHWAY_JSON"u8.ToArray();
        var msgId = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync("HW.SEND", "it.dead.op.queue", msgId, poisonBytes);

        // Run processor node with AllowSuccess=false so it fails attempts and dead-letters
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-op-poison",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await WaitForAsync(async () => await node.DeadLetters.CountAsync<OpQueueMessage>() >= 1, timeoutMs: 8000);

        var letters = await node.DeadLetters.PeekAsync<OpQueueMessage>();
        letters.Should().NotBeEmpty();

        var poisonLetter = letters.FirstOrDefault(l => l.Id == msgId);
        poisonLetter.Should().NotBeNull();
        // Requirement 4: Message = null, RawPayload preserved, no exception thrown
        poisonLetter!.Message.Should().BeNull();
        poisonLetter.RawPayload.ToArray().Should().Equal(poisonBytes);
    }
}
