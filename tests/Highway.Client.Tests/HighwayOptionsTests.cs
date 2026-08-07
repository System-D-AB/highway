using FluentAssertions;
using Highway.Client;
using Xunit;

namespace Highway.Client.Tests;

public class HighwayOptionsTests
{
    [Fact]
    public void Server_DefaultsToNull_ForLocalMode()
    {
        var options = new HighwayOptions();
        options.Server.Should().BeNull();
    }

    [Fact]
    public void CallTimeout_DefaultsTo30Seconds()
    {
        var options = new HighwayOptions();
        options.CallTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}
