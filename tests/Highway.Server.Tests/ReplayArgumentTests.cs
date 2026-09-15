using FluentAssertions;
using Highway.Server.Commands.Ported;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 002 — HW.REPLAY timestamp parsing. Relative offsets are what an
/// operator actually types during an incident, so they get the most coverage.
/// </summary>
public class ReplayArgumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("-30s", 30)]
    [InlineData("-30sec", 30)]
    [InlineData("-30secs", 30)]
    public void RelativeSeconds(string raw, int seconds)
    {
        HwReplayTime.TryParseTimestamp(raw, Now, out var result).Should().BeTrue();
        result.Should().Be(Now.AddSeconds(-seconds));
    }

    [Theory]
    [InlineData("-5m", 5)]
    [InlineData("-5min", 5)]
    [InlineData("-5mins", 5)]
    public void RelativeMinutes(string raw, int minutes)
    {
        HwReplayTime.TryParseTimestamp(raw, Now, out var result).Should().BeTrue();
        result.Should().Be(Now.AddMinutes(-minutes));
    }

    [Theory]
    [InlineData("-2h", 2)]
    [InlineData("-2hr", 2)]
    [InlineData("-2hrs", 2)]
    public void RelativeHours(string raw, int hours)
    {
        HwReplayTime.TryParseTimestamp(raw, Now, out var result).Should().BeTrue();
        result.Should().Be(Now.AddHours(-hours));
    }

    [Theory]
    [InlineData("-1d")]
    [InlineData("-1day")]
    [InlineData("-1days")]
    public void RelativeDays(string raw)
    {
        HwReplayTime.TryParseTimestamp(raw, Now, out var result).Should().BeTrue();
        result.Should().Be(Now.AddDays(-1));
    }

    [Fact]
    public void AbsoluteIso8601()
    {
        HwReplayTime.TryParseTimestamp("2026-08-07T11:30:00Z", Now, out var result).Should().BeTrue();
        result.UtcDateTime.Should().Be(new DateTime(2026, 8, 7, 11, 30, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("-5")]          // no unit
    [InlineData("-5years")]     // unsupported unit
    [InlineData("-abc")]
    [InlineData("not-a-time")]
    public void RejectsMalformed(string raw)
        => HwReplayTime.TryParseTimestamp(raw, Now, out _).Should().BeFalse();
}
