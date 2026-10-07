using System.Threading;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using Xunit;

namespace Highway.Integration.Tests;

[Queue("it.renewal.queue")]
public sealed class ItRenewalQueueMessage : ISend
{
    public int DelayMs { get; set; }
}

public sealed class ItRenewalQueueProcessor : IProcess<ItRenewalQueueMessage>
{
    public static int ExecutionCount;
    public static int CompletedCount;

    public async Task ProcessAsync(ItRenewalQueueMessage message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref ExecutionCount);
        await Task.Delay(message.DelayMs, CancellationToken.None);   // ignores the cap's cancellation on purpose
        Interlocked.Increment(ref CompletedCount);
    }
}

/// <summary>
/// 074 (3.0; ported from 2.6.0 Part B, case B11), end to end with two engines: a handler that runs past MaxProcessingTime
/// (and ignores the cancellation) keeps its lease renewed until it stops, so the idle second worker never
/// gets the same message. Rewritten at review: the first version's handler finished before an expired lease
/// could ever be swept, so it passed even with renewal removed.
/// </summary>
public class RenewalPastCapIntegrationTests
{
    [Fact]
    public async Task RenewalContinuesPastCap_PreventsDuplicateExecutionBySecondWorker()
    {
        ItRenewalQueueProcessor.ExecutionCount = 0;
        ItRenewalQueueProcessor.CompletedCount = 0;

        // 1 s lease: if renewal stopped at the 500 ms cap, the lease would lapse at ~1.4 s and the idle worker's
        // next poll (at most every 2.5 s) would sweep it and run the message a second time while it still runs.
        using var server = new HighwayTestServer(o => o.Lease = TimeSpan.FromSeconds(1));

        void Tune(HighwayOptions o)
        {
            o.WorkerConcurrency = 1;
            o.LeaseRenewalInterval = TimeSpan.FromMilliseconds(200);
            o.MaxProcessingTime = TimeSpan.FromMilliseconds(500);
            o.AbandonGrace = TimeSpan.FromSeconds(30);   // the 8 s handler finishes well inside the grace
        }

        await using var node1 = await EngineNode.StartAsync(server.ConnectionString, "renewal-worker-1", Tune);
        await using var node2 = await EngineNode.StartAsync(server.ConnectionString, "renewal-worker-2", Tune);

        await node1.Client.SendAsync(new ItRenewalQueueMessage { DelayMs = 8000 });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Volatile.Read(ref ItRenewalQueueProcessor.CompletedCount) < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await Task.Delay(1000);   // a second copy, had one started, would be counted by now

        ItRenewalQueueProcessor.CompletedCount.Should().BeGreaterThanOrEqualTo(1, "the handler finishes");
        ItRenewalQueueProcessor.ExecutionCount.Should().Be(1,
            "renewal keeps the lease alive past the cap until the handler stops, so the other worker never runs it");
    }
}
