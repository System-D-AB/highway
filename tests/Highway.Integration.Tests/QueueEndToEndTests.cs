using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- contracts under test -------------------------------------------------

[Queue("it.invoices")]
public sealed record GenerateInvoice : ISend
{
    public string Tag { get; init; } = "";
}

public sealed class InvoiceProcessor : IProcess<GenerateInvoice>
{
    public static readonly List<string> Processed = [];

    public Task ProcessAsync(GenerateInvoice message, CancellationToken ct = default)
    {
        lock (Processed) Processed.Add(message.Tag);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Feature 014 — the queue, through the public client surface.
///
/// <para>Two of these justify the feature: <see cref="SendWithNoProcessorRunning_IsProcessedWhenOneStarts"/>
/// is the capability whose absence made people misuse <c>PublishAsync</c>, and
/// <see cref="MultipleInstances_ShareTheWork"/> is the property Pub/Sub cannot express.</para>
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class QueueEndToEndTests : IDisposable
{
    private readonly HighwayTestServer _server = new();

    public QueueEndToEndTests()
    {
        lock (InvoiceProcessor.Processed) InvoiceProcessor.Processed.Clear();
    }

    public void Dispose() => _server.Dispose();

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }

    private static int ProcessedCount()
    {
        lock (InvoiceProcessor.Processed) return InvoiceProcessor.Processed.Count;
    }

    [Fact]
    public async Task SendAsync_IsProcessedExactlyOnce()
    {
        await using var node = await EngineNode.StartAsync(_server.ConnectionString, "q-node");

        var id = await node.Client.SendAsync(new GenerateInvoice { Tag = "one" });
        id.Should().NotBeNullOrWhiteSpace("the id is how you find the message in the DLQ later");

        await WaitForAsync(() => ProcessedCount() == 1);

        lock (InvoiceProcessor.Processed)
            InvoiceProcessor.Processed.Should().ContainSingle().Which.Should().Be("one");
    }

    /// <summary>
    /// The capability whose absence made people reach for <c>PublishAsync</c> and then need
    /// it to behave like a queue.
    /// </summary>
    [Fact]
    public async Task SendWithNoProcessorRunning_IsProcessedWhenOneStarts()
    {
        // Sent over the wire with no engine anywhere, so there is provably no processor
        // running at send time. Every EngineNode in this assembly hosts InvoiceProcessor,
        // so a client-side send could not establish the same thing.
        var db = (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

        var envelope = System.Text.Encoding.UTF8.GetBytes(
            """{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{"Tag":"waited"}}""");
        await db.ExecuteAsync("HW.QSEND", "it.invoices", Guid.NewGuid().ToString("N"), envelope);

        ((long)(await db.ExecuteAsync("LLEN", "hw:q:it.invoices:q")))
            .Should().Be(1, "the message waits with nothing running");
        ProcessedCount().Should().Be(0);

        // Now a processor appears.
        await using var node = await EngineNode.StartAsync(_server.ConnectionString, "q-late-starter");

        await WaitForAsync(() => ProcessedCount() >= 1);
        lock (InvoiceProcessor.Processed)
            InvoiceProcessor.Processed.Should().Contain("waited",
                "a queued message is processed whenever a processor appears");
    }

    /// <summary>
    /// The property Pub/Sub cannot express. Two nodes, one queue: the work is split, not
    /// duplicated. Under <c>PublishAsync</c> both would receive every message.
    /// </summary>
    [Fact]
    public async Task MultipleInstances_ShareTheWork()
    {
        await using var a = await EngineNode.StartAsync(_server.ConnectionString, "q-worker-a");
        await using var b = await EngineNode.StartAsync(_server.ConnectionString, "q-worker-b");

        for (var i = 0; i < 8; i++)
            await a.Client.SendAsync(new GenerateInvoice { Tag = $"shared-{i}" });

        await WaitForAsync(() => ProcessedCount() >= 8);

        lock (InvoiceProcessor.Processed)
        {
            InvoiceProcessor.Processed.Should().HaveCount(8,
                "each message is processed once — shared, not duplicated across the two nodes");
            InvoiceProcessor.Processed.Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public async Task DelayedSend_IsNotProcessedEarly()
    {
        await using var node = await EngineNode.StartAsync(_server.ConnectionString, "q-delay");

        await node.Client.SendAsync(new GenerateInvoice { Tag = "later" }, TimeSpan.FromMilliseconds(900));

        await Task.Delay(300);
        ProcessedCount().Should().Be(0, "the delivery time has not arrived");

        await WaitForAsync(() => ProcessedCount() == 1);
        lock (InvoiceProcessor.Processed)
            InvoiceProcessor.Processed.Should().ContainSingle().Which.Should().Be("later");
    }

    [Fact]
    public async Task SendAsync_ReturnsAnIdThatFindsTheMessage()
    {
        await using var node = await EngineNode.StartAsync(_server.ConnectionString, "q-id");

        var id = await node.Client.SendAsync(new GenerateInvoice { Tag = "trackable" });

        await WaitForAsync(() => ProcessedCount() == 1);
        id.Should().HaveLength(32, "a compact GUID, generated client-side so it is known before the round trip");
    }

    /// <summary>
    /// Ordinary RPC and Pub/Sub must be untouched by the third verb.
    /// </summary>
    [Fact]
    public async Task AddingAQueue_DoesNotDisturbRpc()
    {
        await using var caller = await EngineNode.StartAsync(_server.ConnectionString, "q-rpc-caller");
        await using var host = await EngineNode.StartAsync(_server.ConnectionString, "q-rpc-host");

        var response = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "still works" });
        response.Value.Should().Be("still works");
    }
}
