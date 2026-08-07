using FluentAssertions;
using Highway.Client;
using Highway.Server;
using Xunit;

namespace Highway.Integration.Tests;

public class SmokeTests
{
    [Fact]
    public void ClientAndServer_CanBeReferenced()
    {
        // Verifies both packages are referenceable together
        var options = new HighwayOptions();
        var builder = new HighwayServerBuilder();

        options.Should().NotBeNull();
        builder.Should().NotBeNull();
    }
}
