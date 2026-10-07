using FluentAssertions;
using Highway.Abstractions;
using Xunit;

namespace Highway.Abstractions.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData("none", BackoffKind.None, 0, 0, false)]
    [InlineData("", BackoffKind.None, 0, 0, false)]
    [InlineData(null, BackoffKind.None, 0, 0, false)]
    [InlineData("fixed:30s", BackoffKind.Fixed, 30_000, 30_000, false)]
    [InlineData("fixed:500ms", BackoffKind.Fixed, 500, 500, false)]
    [InlineData("fixed:1m", BackoffKind.Fixed, 60_000, 60_000, false)]
    [InlineData("fixed:2h", BackoffKind.Fixed, 7_200_000, 7_200_000, false)]
    [InlineData("fixed:1d", BackoffKind.Fixed, 86_400_000, 86_400_000, false)]
    [InlineData("fixed:10s jitter", BackoffKind.Fixed, 10_000, 10_000, true)]
    [InlineData("exp:1s..5m", BackoffKind.Exponential, 1_000, 300_000, false)]
    [InlineData("exp:100ms..10s jitter", BackoffKind.Exponential, 100, 10_000, true)]
    [InlineData("exp:500ms..2m", BackoffKind.Exponential, 500, 120_000, false)]
    // The documented form (user guide, design docs): jitter as a ":jitter" suffix (073 fix).
    [InlineData("exp:500ms..30s:jitter", BackoffKind.Exponential, 500, 30_000, true)]
    [InlineData("fixed:10s:jitter", BackoffKind.Fixed, 10_000, 10_000, true)]
    [InlineData("EXP:1s..5m:JITTER", BackoffKind.Exponential, 1_000, 300_000, true)]
    public void ParseBackoff_ValidStrings_ParsesCorrectly(
        string? input, BackoffKind expectedKind, double expectedMinMs, double expectedMaxMs, bool expectedJitter)
    {
        var (kind, min, max, jitter) = RetryPolicy.ParseBackoff(input);

        kind.Should().Be(expectedKind);
        min.TotalMilliseconds.Should().Be(expectedMinMs);
        max.TotalMilliseconds.Should().Be(expectedMaxMs);
        jitter.Should().Be(expectedJitter);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("fixed")]
    [InlineData("fixed:")]
    [InlineData("fixed:0s")]
    [InlineData("fixed:-5s")]
    [InlineData("fixed:1e30d")]          // overflow: a FormatException, not an OverflowException (073 fix)
    [InlineData("exp:1s..Infinitys")]
    [InlineData("fixed:9000000d")]       // parses as a TimeSpan, but now + it would overflow: bounded at 365 days
    [InlineData("exp")]
    [InlineData("exp:1s")]
    [InlineData("exp:5m..1s")] // max < min
    [InlineData("exp:0s..5s")] // min <= 0
    [InlineData("exp:1s..5s extra")] // unknown token
    public void ParseBackoff_InvalidStrings_ThrowsFormatException(string input)
    {
        var act = () => RetryPolicy.ParseBackoff(input);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void CalculateBackoffTicks_FixedSchedule_CalculatesExpected()
    {
        var policy = new RetryPolicy(5, BackoffKind.Fixed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), false, 3);

        policy.CalculateBackoffTicks("msg-1", 1).Should().Be(TimeSpan.FromSeconds(2).Ticks);
        policy.CalculateBackoffTicks("msg-1", 2).Should().Be(TimeSpan.FromSeconds(2).Ticks);
        policy.CalculateBackoffTicks("msg-1", 3).Should().Be(TimeSpan.FromSeconds(2).Ticks);
    }

    [Fact]
    public void CalculateBackoffTicks_ExponentialSchedule_DoublesAndCaps()
    {
        var policy = new RetryPolicy(5, BackoffKind.Exponential, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), false, 3);

        // Attempt 1: min (1s)
        policy.CalculateBackoffTicks("msg-1", 1).Should().Be(TimeSpan.FromSeconds(1).Ticks);

        // Attempt 2: min * 2 = 2s
        policy.CalculateBackoffTicks("msg-1", 2).Should().Be(TimeSpan.FromSeconds(2).Ticks);

        // Attempt 3: min * 4 = 4s
        policy.CalculateBackoffTicks("msg-1", 3).Should().Be(TimeSpan.FromSeconds(4).Ticks);

        // Attempt 4: min * 8 = 8s
        policy.CalculateBackoffTicks("msg-1", 4).Should().Be(TimeSpan.FromSeconds(8).Ticks);

        // Attempt 5: min * 16 = 16s capped at max (10s)
        policy.CalculateBackoffTicks("msg-1", 5).Should().Be(TimeSpan.FromSeconds(10).Ticks);

        // Attempt 10: capped at max (10s)
        policy.CalculateBackoffTicks("msg-1", 10).Should().Be(TimeSpan.FromSeconds(10).Ticks);
    }

    [Fact]
    public void CalculateBackoffTicks_WithJitter_IsDeterministicAndWithin20Percent()
    {
        var policy = new RetryPolicy(5, BackoffKind.Exponential, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), true, 3);

        var first = policy.CalculateBackoffTicks("order-12345", 2);
        var second = policy.CalculateBackoffTicks("order-12345", 2);

        // Determinism: exactly identical ticks every time
        first.Should().Be(second);

        // Base for attempt 2 is 20s
        var baseTicks = TimeSpan.FromSeconds(20).Ticks;
        var minAllowed = (long)(baseTicks * 0.80);
        var maxAllowed = (long)(baseTicks * 1.20);

        first.Should().BeInRange(minAllowed, maxAllowed);

        // Different message ID produces distinct jitter
        var differentMsg = policy.CalculateBackoffTicks("order-99999", 2);
        differentMsg.Should().BeInRange(minAllowed, maxAllowed);
        differentMsg.Should().NotBe(first);
    }

    [Fact]
    public void FromAttribute_ValidAttribute_ParsesProperties()
    {
        var attr = new RetryAttribute
        {
            MaxAttempts = 4,
            Backoff = "exp:500ms..30s jitter",
            MaxRequeues = 2
        };

        var policy = RetryPolicy.FromAttribute(attr);
        policy.MaxAttempts.Should().Be(4);
        policy.BackoffKind.Should().Be(BackoffKind.Exponential);
        policy.MinBackoff.Should().Be(TimeSpan.FromMilliseconds(500));
        policy.MaxBackoff.Should().Be(TimeSpan.FromSeconds(30));
        policy.Jitter.Should().BeTrue();
        policy.MaxRequeues.Should().Be(2);
    }

    [Fact]
    public void Validate_InvalidAttribute_ThrowsInvalidRetryPolicyException()
    {
        var attr = new RetryAttribute
        {
            MaxAttempts = -1,
            Backoff = "invalid"
        };

        var act = () => RetryPolicy.Validate(attr, typeof(RetryPolicyTests));
        act.Should().Throw<InvalidRetryPolicyException>()
            .WithMessage("*RetryPolicyTests*");
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(3, -1)]
    public void Validate_NegativeAttemptsOrRequeues_WithAValidBackoff_IsRefused(int maxAttempts, int maxRequeues)
    {
        // The test above also has a malformed backoff, so it throws whether or not the counts are checked.
        var attr = new RetryAttribute { MaxAttempts = maxAttempts, Backoff = "fixed:1s", MaxRequeues = maxRequeues };

        var act = () => RetryPolicy.Validate(attr, typeof(RetryPolicyTests));

        act.Should().Throw<InvalidRetryPolicyException>().WithMessage("*negative*");
    }
}
