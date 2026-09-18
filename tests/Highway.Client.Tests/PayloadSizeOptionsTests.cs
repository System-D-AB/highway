using FluentAssertions;
using Highway.Client;
using Xunit;

namespace Highway.Client.Tests;

/// <summary>
/// Feature 057 T2 — the client's message-size limit is configurable (default 5 MiB), bounded by the
/// 15 MiB ceiling, and the *effective* limit prefers the server's learned value (057-b) over the
/// configured one so a raised server is honoured with no client change.
/// </summary>
public class PayloadSizeOptionsTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void Default_Is5MiB()
        => new HighwayOptions().MaxPayloadBytes.Should().Be(5 * MiB);

    [Fact]
    public void EffectiveLimit_IsTheConfiguredValue_UntilLearned()
    {
        var o = new HighwayOptions { MaxPayloadBytes = 5 * MiB };
        o.EffectiveMaxPayloadBytes.Should().Be(5 * MiB);
    }

    [Fact]
    public void EffectiveLimit_PrefersTheLearnedServerValue()
    {
        var o = new HighwayOptions { MaxPayloadBytes = 5 * MiB, LearnedServerMaxPayloadBytes = 10 * MiB };
        o.EffectiveMaxPayloadBytes.Should().Be(10 * MiB, "the server is the authority; the client tracks it");
    }

    [Fact]
    public void Validate_RefusesAboveThe15MiBCeiling()
    {
        var act = () => HighwayOptionsValidator.Validate(
            new HighwayOptions { NodeName = "n", MaxPayloadBytes = 16 * MiB });
        act.Should().Throw<InvalidOperationException>().WithMessage("*15 MiB ceiling*");
    }

    [Fact]
    public void Validate_RefusesNonPositive()
    {
        var act = () => HighwayOptionsValidator.Validate(
            new HighwayOptions { NodeName = "n", MaxPayloadBytes = 0 });
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be positive*");
    }
}
