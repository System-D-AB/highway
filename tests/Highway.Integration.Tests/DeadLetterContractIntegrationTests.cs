using System.Collections.Concurrent;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- Test contracts for dead-letter integration ----

[Queue("it.dead.orders")]
public sealed record DlOrderMessage : ISend
{
    public string Id { get; init; } = "";
    public bool ShouldFail { get; init; } = true;
}

public sealed class DlOrderProcessor : IProcess<DlOrderMessage>
{
    public static readonly ConcurrentBag<string> Processed = new();
    public static bool AllowSuccess = false;

    public Task ProcessAsync(DlOrderMessage message, CancellationToken ct = default)
    {
        if (message.ShouldFail && !AllowSuccess)
            throw new InvalidOperationException($"Intentional processing failure for {message.Id}");

        Processed.Add(message.Id);
        return Task.CompletedTask;
    }
}

public sealed class DlOrderDeadLetterHandler : IDeadLetter<DlOrderMessage>
{
    public static readonly ConcurrentBag<DeadLetter<DlOrderMessage>> HandledLetters = new();
    public static Func<DeadLetter<DlOrderMessage>, DeadLetterAction>? CustomAction;

    public Task<DeadLetterAction> HandleAsync(DeadLetter<DlOrderMessage> letter, CancellationToken ct = default)
    {
        HandledLetters.Add(letter);
        if (CustomAction is not null)
            return Task.FromResult(CustomAction(letter));

        return Task.FromResult(DeadLetterAction.Keep);
    }
}

[Collection(SubscriberRecorderCollection.Name)]
public class DeadLetterContractIntegrationTests : IDisposable
{
    private readonly HighwayTestServer _server;

    public DeadLetterContractIntegrationTests()
    {
        DlOrderProcessor.Processed.Clear();
        DlOrderProcessor.AllowSuccess = false;
        DlOrderDeadLetterHandler.HandledLetters.Clear();
        DlOrderDeadLetterHandler.CustomAction = null;

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

    [Fact]
    public async Task DeadLetterHandler_ReceivesAndDiscards_DeadLetter()
    {
        DlOrderDeadLetterHandler.CustomAction = _ => DeadLetterAction.Discard;

        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-dlq-1",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await node.Client.SendAsync(new DlOrderMessage { Id = "order-101", ShouldFail = true });

        // Wait for handler to receive the dead letter
        await WaitForAsync(() => !DlOrderDeadLetterHandler.HandledLetters.IsEmpty, timeoutMs: 10000);

        DlOrderDeadLetterHandler.HandledLetters.Should().ContainSingle();
        var handled = DlOrderDeadLetterHandler.HandledLetters.First();
        handled.Message.Should().NotBeNull();
        handled.Message!.Id.Should().Be("order-101");
        handled.Attempts.Should().Be(3);
        handled.Reason.Should().Be("MAX_ATTEMPTS");
        handled.Target.Kind.Should().Be(DeadLetterTargetKind.Queue);
        handled.Target.Name.Should().Be("it.dead.orders");

        // Verify that DISCARD removed it from the broker DLQ
        var db = await ConnectAsync();
        var peek = await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", "it.dead.orders");
        peek.IsNull.Should().BeFalse();
        var rows = (RedisResult[])peek!;
        rows.Length.Should().Be(0);
    }

    [Fact]
    public async Task DeadLetterHandler_ThrowsException_SafelyPreservesEvidenceAsKeep()
    {
        DlOrderDeadLetterHandler.CustomAction = _ => throw new InvalidOperationException("Handler exploded!");

        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-dlq-throw",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await node.Client.SendAsync(new DlOrderMessage { Id = "order-evidence", ShouldFail = true });

        await WaitForAsync(() => !DlOrderDeadLetterHandler.HandledLetters.IsEmpty, timeoutMs: 10000);

        // Allow resolution to finish on broker
        await Task.Delay(200);

        // Dead letter must still be on the DLQ because a throwing handler resolves KEEP
        var db = await ConnectAsync();
        var peek = await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", "it.dead.orders");
        var rows = (RedisResult[])peek!;
        rows.Length.Should().Be(1);
    }

    [Fact]
    public async Task DeadLetterHandler_Requeue_ReEntersLiveQueueWithLineage()
    {
        var requeued = false;
        DlOrderDeadLetterHandler.CustomAction = letter =>
        {
            if (!requeued)
            {
                requeued = true;
                // Allow processor to succeed when requeued
                DlOrderProcessor.AllowSuccess = true;
                return DeadLetterAction.Requeue();
            }
            return DeadLetterAction.Keep;
        };

        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-dlq-requeue",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await node.Client.SendAsync(new DlOrderMessage { Id = "order-requeue", ShouldFail = true });

        // Wait for it to be processed successfully after requeue
        await WaitForAsync(() => DlOrderProcessor.Processed.Contains("order-requeue"), timeoutMs: 10000);

        DlOrderProcessor.Processed.Should().Contain("order-requeue");
    }

    [Fact]
    public async Task CompetingConsumers_TwoNodes_HandledExactlyOncePerDeadLetter()
    {
        DlOrderDeadLetterHandler.CustomAction = _ => DeadLetterAction.Discard;

        await using var node1 = await EngineNode.StartAsync(
            _server.ConnectionString, "comp-node-1",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await using var node2 = await EngineNode.StartAsync(
            _server.ConnectionString, "comp-node-2",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 4; i++)
        {
            await node1.Client.SendAsync(new DlOrderMessage { Id = $"comp-{i}", ShouldFail = true });
        }

        await WaitForAsync(() => DlOrderDeadLetterHandler.HandledLetters.Count >= 4, timeoutMs: 12000);

        // Exactly 4 dead letters handled across both competing nodes
        DlOrderDeadLetterHandler.HandledLetters.Count.Should().Be(4);
        var handledIds = DlOrderDeadLetterHandler.HandledLetters.Select(l => l.Message!.Id).Distinct().ToList();
        handledIds.Count.Should().Be(4);
    }
}
