using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

[Queue("e2e.poison")]
public sealed record PoisonWork : ISend
{
    public string Order { get; init; } = "";
}

/// <summary>Always throws, with a message specific enough to recognise in a dead letter.</summary>
public sealed class PoisonProcessor : IProcess<PoisonWork>
{
    public Task ProcessAsync(PoisonWork message, CancellationToken ct = default)
        => throw new InvalidOperationException($"order {message.Order} is already shipped");
}

/// <summary>
/// Feature 015 T12 — the whole feature, end to end, with nothing mocked.
///
/// <para>Handler throws → <c>FailureReporter</c> → <c>HW.FAIL</c> → the entry is rewritten →
/// the lease expires → the sweep requeues carrying the block → attempts exhaust → dead letter
/// → <c>HW.DLQ PEEK</c> shows what threw. Mocking any single hop would hide exactly the
/// failure this feature exists to surface, and one of those hops was in fact broken until the
/// two-worker test caught it.</para>
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class FailureEndToEndTests : IDisposable
{
    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(200);
        o.MaxDeliveryAttempts = 1;
    });

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task DeadLetter_CarriesExceptionTypeMessageAndStack()
    {
        var db = (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

        await using (await EngineNode.StartAsync(_server.ConnectionString, "e2e-node"))
        {
            await db.ExecuteAsync("HW.QSEND", "e2e.poison", "order-77",
                System.Text.Encoding.UTF8.GetBytes(
                    """{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{"Order":"order-77"}}"""));

            // The worker claims it, throws, reports, and never acknowledges. Repeated claims
            // drive the sweep until the attempts are exhausted.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if ((long)await db.ExecuteAsync("LLEN", "hw:q:e2e.poison:dlq") > 0) break;
                await Task.Delay(150);
                await db.ExecuteAsync("HW.QCLAIM", "e2e.poison", "sweeper");
            }
        }

        var peeked = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "Q", "e2e.poison"))!;
        peeked.Should().HaveCount(1, "the handler throws every time, so the message must dead-letter");

        var flat = (RedisResult[])peeked[0]!;
        var dead = new Dictionary<string, string>();
        for (var i = 0; i + 1 < flat.Length; i += 2)
            dead[flat[i].ToString()!] = flat[i + 1].ToString()!;

        // This is the sentence the feature exists to produce. Before it, a dead letter said
        // only that something failed n times.
        dead.Should().ContainKey("failureType").WhoseValue
            .Should().Be("System.InvalidOperationException");

        dead["failureDetail"].Should().Contain("order order-77 is already shipped",
            "the exception message must reach the operator without correlating worker logs");
        dead["failureDetail"].Should().Contain("stack",
            "and the stack, which is what says WHERE it threw");
        dead["failureDetail"].Should().Contain("e2e-node",
            "and which node was holding it");
    }
}
