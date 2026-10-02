using FluentAssertions;
using Xunit;

namespace Highway.Abstractions.Tests;

public class DeadLetterContractTests
{
    private sealed class SampleCommand : ISend;

    [Fact]
    public void DeadLetterAction_Factories_ReturnExpectedProperties()
    {
        var keep = DeadLetterAction.Keep;
        keep.Type.Should().Be(DeadLetterAction.ActionType.Keep);
        keep.After.Should().BeNull();

        var discard = DeadLetterAction.Discard;
        discard.Type.Should().Be(DeadLetterAction.ActionType.Discard);
        discard.After.Should().BeNull();

        var requeueImmediate = DeadLetterAction.Requeue();
        requeueImmediate.Type.Should().Be(DeadLetterAction.ActionType.Requeue);
        requeueImmediate.After.Should().BeNull();

        var delay = TimeSpan.FromSeconds(30);
        var requeueDelayed = DeadLetterAction.Requeue(delay);
        requeueDelayed.Type.Should().Be(DeadLetterAction.ActionType.Requeue);
        requeueDelayed.After.Should().Be(delay);
    }

    [Fact]
    public void DeadLetterTarget_Formatting_IsReadable()
    {
        var queue = new DeadLetterTarget(DeadLetterTargetKind.Queue, "orders");
        queue.ToString().Should().Be("Q:orders");

        var svc = new DeadLetterTarget(DeadLetterTargetKind.Service, "billing.charge");
        svc.ToString().Should().Be("SVC:billing.charge");

        var grp = new DeadLetterTarget(DeadLetterTargetKind.Group, "events", "billing");
        grp.ToString().Should().Be("Q:events@billing");
    }

    [Fact]
    public void DeadLetterRecord_CarriesPropertiesAccurately()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var now = DateTimeOffset.UtcNow;
        var cmd = new SampleCommand();
        var target = new DeadLetterTarget(DeadLetterTargetKind.Queue, "orders");
        var lastFail = new FailureInfo("System.InvalidOperationException", "error occurred");
        var firstFail = new FailureInfo("System.TimeoutException", "timeout occurred");

        var letter = new DeadLetter<SampleCommand>(
            cmd,
            payload,
            "msg-123",
            "000000100000000000000001",
            target,
            Attempts: 5,
            Requeues: 1,
            now,
            "MAX_ATTEMPTS",
            lastFail,
            firstFail);

        letter.Message.Should().BeSameAs(cmd);
        letter.RawPayload.ToArray().Should().Equal(payload);
        letter.Id.Should().Be("msg-123");
        letter.DeadLetterId.Should().Be("000000100000000000000001");
        letter.Target.Should().Be(target);
        letter.Attempts.Should().Be(5);
        letter.Requeues.Should().Be(1);
        letter.DeadLetteredAt.Should().Be(now);
        letter.Reason.Should().Be("MAX_ATTEMPTS");
        letter.LastFailure.Should().Be(lastFail);
        letter.FirstFailure.Should().Be(firstFail);
    }

    [Fact]
    public void RetryAttribute_Defaults_AreStandard()
    {
        var attr = new RetryAttribute();
        attr.MaxAttempts.Should().Be(0);
        attr.Backoff.Should().Be("none");
        attr.MaxRequeues.Should().Be(3);

        var customized = new RetryAttribute
        {
            MaxAttempts = 8,
            Backoff = "exp:1s..5m jitter",
            MaxRequeues = 5
        };
        customized.MaxAttempts.Should().Be(8);
        customized.Backoff.Should().Be("exp:1s..5m jitter");
        customized.MaxRequeues.Should().Be(5);
    }
}
