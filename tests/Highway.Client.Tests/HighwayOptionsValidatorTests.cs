using FluentAssertions;
using Highway.Client;
using Xunit;

namespace Highway.Client.Tests;

/// <summary>
/// Feature 005 Task 2 — HighwayOptions defaults and validation.
/// </summary>
public class HighwayOptionsValidatorTests
{
    // -------------------------------------------------------------------------
    // Defaults
    // -------------------------------------------------------------------------

    [Fact]
    public void Defaults_AreProductionReady()
    {
        var o = new HighwayOptions();

        o.WorkerConcurrency.Should().Be(8);
        o.ReceiveBatchSize.Should().Be(10);
        o.BackstopInterval.Should().Be(TimeSpan.FromMilliseconds(500));
        o.DrainTimeout.Should().Be(TimeSpan.FromSeconds(10));
        o.DoorbellsEnabled.Should().BeTrue();
    }

    [Fact]
    public void NodeName_Default_IsStable_NotRandom()
    {
        var a = new HighwayOptions();
        var b = new HighwayOptions();

        a.NodeName.Should().Be(b.NodeName, "the default must be deterministic across instances");
        a.NodeName.Should().NotBeNullOrWhiteSpace();
        a.NodeName.Should().Contain(Environment.MachineName);
    }

    [Fact]
    public void ValidOptions_Pass()
    {
        var o = new HighwayOptions { Server = "localhost:6500" };
        var act = () => HighwayOptionsValidator.Validate(o);
        act.Should().NotThrow();
    }

    // -------------------------------------------------------------------------
    // NodeName rules (client half of 004.1 Requirement 3)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NodeName_Blank_Throws(string? name)
    {
        var act = () => HighwayOptionsValidator.ValidateNodeName(name);
        act.Should().Throw<InvalidOperationException>().WithMessage("*NodeName*blank*");
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a\u007fb")]
    public void NodeName_ControlCharacter_Throws(string name)
    {
        var act = () => HighwayOptionsValidator.ValidateNodeName(name);
        act.Should().Throw<InvalidOperationException>().WithMessage("*control character*");
    }

    [Fact]
    public void NodeName_Over256Bytes_Throws()
    {
        var act = () => HighwayOptionsValidator.ValidateNodeName(new string('a', 257));
        act.Should().Throw<InvalidOperationException>().WithMessage("*257*256*");
    }

    [Fact]
    public void NodeName_AtLimit_Passes()
    {
        var act = () => HighwayOptionsValidator.ValidateNodeName(new string('a', 256));
        act.Should().NotThrow();
    }

    // -------------------------------------------------------------------------
    // Numeric/timespan rules
    // -------------------------------------------------------------------------

    [Fact]
    public void CallTimeout_Zero_Throws()
    {
        var o = new HighwayOptions { CallTimeout = TimeSpan.Zero };
        var act = () => HighwayOptionsValidator.Validate(o);
        act.Should().Throw<InvalidOperationException>().WithMessage("*CallTimeout*positive*");
    }

    [Fact]
    public void WorkerConcurrency_Zero_Throws()
    {
        var o = new HighwayOptions { WorkerConcurrency = 0 };
        var act = () => HighwayOptionsValidator.Validate(o);
        act.Should().Throw<InvalidOperationException>().WithMessage("*WorkerConcurrency*at least 1*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void ReceiveBatchSize_OutOfBounds_Throws(int size)
    {
        var o = new HighwayOptions { ReceiveBatchSize = size };
        var act = () => HighwayOptionsValidator.Validate(o);
        act.Should().Throw<InvalidOperationException>().WithMessage("*ReceiveBatchSize*");
    }

    [Fact]
    public void BackstopInterval_TooSmall_Throws()
    {
        var o = new HighwayOptions { BackstopInterval = TimeSpan.FromMilliseconds(10) };
        var act = () => HighwayOptionsValidator.Validate(o);
        act.Should().Throw<InvalidOperationException>().WithMessage("*BackstopInterval*50ms*");
    }

    [Fact]
    public void DrainTimeout_Negative_Throws()
    {
        var o = new HighwayOptions { DrainTimeout = TimeSpan.FromSeconds(-1) };
        var act = () => HighwayOptionsValidator.Validate(o);
        act.Should().Throw<InvalidOperationException>().WithMessage("*DrainTimeout*positive*");
    }
}
