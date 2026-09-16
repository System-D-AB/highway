using FluentAssertions;
using Highway.Server;
using Xunit;

namespace Highway.Server.Tests;

public class HighwayReplicationOptionsTests
{
    [Fact]
    public void Validate_AutoFailover_RejectsPromoteNotGreaterThanFencePlusMargin()
    {
        var opts = new HighwayReplicationOptions
        {
            AutoFailover = true,
            FenceTimeout = TimeSpan.FromSeconds(5),
            PromoteTimeout = TimeSpan.FromSeconds(6),
            Margin = TimeSpan.FromSeconds(1),
        };

        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*PromoteTimeout*");
    }

    [Fact]
    public void Validate_AutoFailover_AcceptsOd1Defaults()
    {
        var opts = new HighwayReplicationOptions { AutoFailover = true };
        opts.Validate();
        opts.FenceTimeout.Should().Be(TimeSpan.FromSeconds(5));
        opts.PromoteTimeout.Should().Be(TimeSpan.FromSeconds(8));
        opts.Margin.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Validate_WithoutAutoFailover_IgnoresBadTriple()
    {
        var opts = new HighwayReplicationOptions
        {
            AutoFailover = false,
            FenceTimeout = TimeSpan.FromSeconds(10),
            PromoteTimeout = TimeSpan.FromSeconds(1),
        };
        opts.Validate();
    }
}
