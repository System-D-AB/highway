using FluentAssertions;
using Highway.Server.Host.Configuration;
using Xunit;

namespace Highway.Server.Host.Tests;

public class SizeFormatTests
{
    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("1k", 1024)]
    [InlineData("512m", 512L * 1024 * 1024)]
    [InlineData("1g", 1024L * 1024 * 1024)]
    [InlineData("2gb", 2L * 1024 * 1024 * 1024)]
    [InlineData(" 512m ", 512L * 1024 * 1024)]
    [InlineData("0", 0)]
    public void Parse_ReadsNumbersAndSuffixes(string text, long expected)
        => SizeFormat.Parse(text, "test").Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("m")]
    [InlineData("12x")]
    [InlineData("-1g")]
    [InlineData("1.5g")]
    public void Parse_RefusesGarbage_NamingTheContext(string text)
    {
        var act = () => SizeFormat.Parse(text, "server.aofSizeLimitBytes");

        act.Should().Throw<ConfigurationException>()
           .WithMessage("*server.aofSizeLimitBytes*");
    }
}
