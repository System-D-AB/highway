using FluentAssertions;
using Xunit;

namespace Highway.Server.Host.Tests;

public class HostArgumentsTests
{
    [Theory]
    [InlineData("--install")]
    [InlineData("--uninstall")]
    [InlineData("--status")]
    [InlineData("--start")]
    [InlineData("--stop")]
    public void AVerbInFirstPosition_IsTheVerb(string verb)
    {
        var parsed = HostArguments.Parse([verb]);

        parsed.Verb.Should().Be(verb);
    }

    [Fact]
    public void InstallAcceptsTheStartModifier()
    {
        var parsed = HostArguments.Parse(["--install", "--start"]);

        parsed.Verb.Should().Be("--install");
        parsed.StartAfterInstall.Should().BeTrue();
    }

    [Fact]
    public void ValueFlags_ConsumeTheirValue()
    {
        var parsed = HostArguments.Parse(
            ["--config", "conf/highway.json", "--port", "6600", "--bind", "0.0.0.0", "--data-dir", "d:"]);

        parsed.ConfigPath.Should().EndWith("highway.json");
        parsed.Port.Should().Be(6600);
        parsed.BindAddress.Should().Be("0.0.0.0");
        parsed.DataDir.Should().Be("d:");
    }

    [Fact]
    public void AnUnknownArgument_IsNamed()
    {
        var act = () => HostArguments.Parse(["--frobnicate"]);

        act.Should().Throw<CommandLineException>().WithMessage("*--frobnicate*");
    }

    [Fact]
    public void AMissingValue_IsNamed()
    {
        var act = () => HostArguments.Parse(["--config"]);

        act.Should().Throw<CommandLineException>().WithMessage("*--config*requires a value*");
    }

    [Fact]
    public void ANonNumericPort_IsNamed()
    {
        var act = () => HostArguments.Parse(["--port", "six"]);

        act.Should().Throw<CommandLineException>().WithMessage("*--port*'six'*");
    }

    [Fact]
    public void RunFlags_CannotRideAlongWithAVerb()
    {
        var act = () => HostArguments.Parse(["--install", "--port", "6600"]);

        act.Should().Throw<CommandLineException>().WithMessage("*--port*--install*");
    }

    [Fact]
    public void NoArguments_IsRunMode()
    {
        var parsed = HostArguments.Parse([]);

        parsed.Verb.Should().BeNull();
        parsed.ShowVersion.Should().BeFalse();
        parsed.Validate.Should().BeFalse();
    }
}
