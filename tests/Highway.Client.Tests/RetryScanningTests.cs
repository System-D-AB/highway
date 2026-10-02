using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Scanning;
using Xunit;

namespace Highway.Client.Tests;

// ---- Test contracts ----

[Queue("test.retry.valid.queue")]
[Retry(MaxAttempts = 4, Backoff = "exp:1s..30s jitter", MaxRequeues = 2)]
public sealed record ValidRetryQueueMessage : ISend;

public sealed class ValidRetryQueueProcessor : IProcess<ValidRetryQueueMessage>
{
    public Task ProcessAsync(ValidRetryQueueMessage message, CancellationToken ct = default) => Task.CompletedTask;
}

[Queue("test.retry.invalid.queue")]
[Retry(MaxAttempts = 3, Backoff = "broken-backoff")]
public sealed record InvalidRetryQueueMessage : ISend;

public sealed class InvalidRetryQueueProcessor : IProcess<InvalidRetryQueueMessage>
{
    public Task ProcessAsync(InvalidRetryQueueMessage message, CancellationToken ct = default) => Task.CompletedTask;
}

[Channel("test.retry.channel")]
[Retry(MaxAttempts = 5, Backoff = "fixed:10s", MaxRequeues = 1)]
public sealed record ValidRetryChannelMessage : IPublish;

public sealed class ValidRetryChannelSubscriber : ISubscribe<ValidRetryChannelMessage>
{
    public Task SubscribeAsync(ValidRetryChannelMessage message, CancellationToken ct = default) => Task.CompletedTask;
}

[Service("test.retry.service")]
[Retry(MaxAttempts = 2, Backoff = "exp:500ms..5s")]
public sealed record ValidRetryServiceRequest : IReturn<ValidRetryServiceResponse>;

public sealed class ValidRetryServiceResponse : Output;

public sealed class ValidRetryServiceHandler : AsyncService<ValidRetryServiceRequest, ValidRetryServiceResponse>
{
    public override Task<ValidRetryServiceResponse> ExecuteAsync(ValidRetryServiceRequest request, CancellationToken ct = default)
        => Task.FromResult(new ValidRetryServiceResponse());
}

public class RetryScanningTests
{
    [Fact]
    public void Scanner_DiscoversAndValidates_QueueRetryPolicy()
    {
        var scanner = new DefaultTypeScanner();
        var result = scanner.ScanTypes([typeof(ValidRetryQueueMessage), typeof(ValidRetryQueueProcessor)]);

        result.Queues.Should().HaveCount(1);
        var q = result.Queues[0];
        q.Retry.Should().NotBeNull();
        q.Retry!.MaxAttempts.Should().Be(4);
        q.Retry.Backoff.Should().Be("exp:1s..30s jitter");
        q.Retry.MaxRequeues.Should().Be(2);
    }

    [Fact]
    public void Scanner_DiscoversAndValidates_ChannelRetryPolicy()
    {
        var scanner = new DefaultTypeScanner();
        var result = scanner.ScanTypes([typeof(ValidRetryChannelMessage), typeof(ValidRetryChannelSubscriber)]);

        result.Channels.Should().HaveCount(1);
        var c = result.Channels[0];
        c.Retry.Should().NotBeNull();
        c.Retry!.MaxAttempts.Should().Be(5);
        c.Retry.Backoff.Should().Be("fixed:10s");
        c.Retry.MaxRequeues.Should().Be(1);
    }

    [Fact]
    public void Scanner_DiscoversAndValidates_ServiceRetryPolicy()
    {
        var scanner = new DefaultTypeScanner();
        var result = scanner.ScanTypes([typeof(ValidRetryServiceRequest), typeof(ValidRetryServiceResponse), typeof(ValidRetryServiceHandler)]);

        result.Services.Should().HaveCount(1);
        var s = result.Services[0];
        s.Retry.Should().NotBeNull();
        s.Retry!.MaxAttempts.Should().Be(2);
        s.Retry.Backoff.Should().Be("exp:500ms..5s");
    }

    [Fact]
    public void Scanner_ThrowsInvalidRetryPolicyException_OnMalformedBackoff()
    {
        var scanner = new DefaultTypeScanner();
        var act = () => scanner.ScanTypes([typeof(InvalidRetryQueueMessage), typeof(InvalidRetryQueueProcessor)]);

        act.Should().Throw<InvalidRetryPolicyException>()
            .WithMessage("*InvalidRetryQueueMessage*");
    }
}
