using FluentAssertions;
using Highway.Server;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 057 T1 — message size is a chosen, bounded envelope: default 5 MiB, refused above the
/// 15 MiB ceiling (a message is buffered whole in memory at several hops, so the bound protects
/// memory, not disk).
/// </summary>
public class PayloadSizeValidationTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void Default_Is5MiB()
        => new HighwayServerOptions().MaxPayloadBytes.Should().Be(5 * MiB);

    [Fact]
    public void ValidateDeliveryOptions_RefusesAboveThe15MiBCeiling()
    {
        var act = () => HighwayServerBuilder.ValidateDeliveryOptions(
            new HighwayServerOptions { MaxPayloadBytes = 16 * MiB });
        act.Should().Throw<InvalidOperationException>().WithMessage("*15 MiB ceiling*");
    }

    [Fact]
    public void ValidateDeliveryOptions_AcceptsExactlyTheCeiling()
    {
        var act = () => HighwayServerBuilder.ValidateDeliveryOptions(
            new HighwayServerOptions { MaxPayloadBytes = 15 * MiB });
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateDeliveryOptions_RefusesNonPositive()
    {
        var act = () => HighwayServerBuilder.ValidateDeliveryOptions(
            new HighwayServerOptions { MaxPayloadBytes = 0 });
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be positive*");
    }
}
