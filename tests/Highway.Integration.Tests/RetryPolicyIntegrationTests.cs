using System.Collections.Concurrent;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using Xunit;

namespace Highway.Integration.Tests;

// ---- Test contract and processor ----

[Queue("it.retry.policy.queue")]
[Retry(MaxAttempts = 2, Backoff = "fixed:400ms")]
public sealed record FailingRetryMessage : ISend
{
    public string Id { get; init; } = "";
}

public sealed class FailingRetryProcessor : IProcess<FailingRetryMessage>
{
    public static readonly ConcurrentBag<(string Id, DateTime Time)> Attempts = new();

    public Task ProcessAsync(FailingRetryMessage message, CancellationToken ct = default)
    {
        Attempts.Add((message.Id, DateTime.UtcNow));
        throw new InvalidOperationException($"Simulated processing failure for {message.Id}");
    }
}

[Collection(SubscriberRecorderCollection.Name)]
public class RetryPolicyIntegrationTests : IDisposable
{
    private readonly HighwayTestServer _server;

    public RetryPolicyIntegrationTests()
    {
        FailingRetryProcessor.Attempts.Clear();
        _server = new HighwayTestServer(o =>
        {
            o.Lease = TimeSpan.FromMilliseconds(50);
            o.MaxDeliveryAttempts = 5; // Server option is 5, but contract policy overrides to 2!
        });
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task FailingProcessor_RetriesWithBackoff_AndDeadLettersWhenMaxAttemptsExhausted()
    {
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "node-retry-test",
            o => o.BackstopInterval = TimeSpan.FromMilliseconds(50));

        await node.Client.SendAsync(new FailingRetryMessage { Id = "retry-msg-1" });

        // Wait for dead letter count to become 1
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && await node.DeadLetters.CountAsync<FailingRetryMessage>() == 0)
        {
            await Task.Delay(50);
        }

        var deadCount = await node.DeadLetters.CountAsync<FailingRetryMessage>();
        deadCount.Should().Be(1, "message must be dead-lettered after exhausting MaxAttempts = 2");

        // Verify attempts were spaced apart by at least the backoff duration (400ms)
        var attempts = FailingRetryProcessor.Attempts.Where(a => a.Id == "retry-msg-1").OrderBy(a => a.Time).ToList();
        attempts.Should().HaveCount(3, "processor must have received initial attempt + 2 retries before DLQ");

        var gap1 = attempts[1].Time - attempts[0].Time;
        gap1.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300),
            "first retry must respect the 400ms backoff policy instead of retrying immediately");

        var gap2 = attempts[2].Time - attempts[1].Time;
        gap2.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300),
            "second retry must respect the 400ms backoff policy instead of retrying immediately");

        // Verify DLQ contents
        var letters = await node.DeadLetters.PeekAsync<FailingRetryMessage>(count: 1);
        letters.Should().HaveCount(1);
        letters[0].Message!.Id.Should().Be("retry-msg-1");
        letters[0].Reason.Should().Be("MAX_ATTEMPTS");
    }
}
