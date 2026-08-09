using System.Net;
using FluentAssertions;
using Highway.Server;
using Xunit;

namespace Highway.Server.Tests;

public class HighwayServerBuilderTests
{
    [Fact]
    public void CanInstantiate()
    {
        var builder = new HighwayServerBuilder();
        builder.Should().NotBeNull();
    }

    // -------------------------------------------------------------------------
    // Feature 004.1 Task 6 — Requirement 8: configurable bind address
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildGarnetOptions_DefaultsToLoopback()
    {
        var garnet = HighwayServerBuilder.BuildGarnetOptions(new HighwayServerOptions());

        garnet.EndPoints.Should().ContainSingle();
        var ep = (IPEndPoint)garnet.EndPoints[0];
        ep.Address.Should().Be(IPAddress.Loopback, "secure by default");
        ep.Port.Should().Be(6500);
    }

    [Fact]
    public void BuildGarnetOptions_BindAddressMapsThrough()
    {
        var opts = new HighwayServerOptions { BindAddress = IPAddress.Any };

        var garnet = HighwayServerBuilder.BuildGarnetOptions(opts);

        ((IPEndPoint)garnet.EndPoints[0]).Address.Should().Be(IPAddress.Any);
    }

    [Fact]
    public void WithBindAddress_ValidString_EndpointReflectsAddress()
    {
        using var server = new HighwayServerBuilder()
            .WithPort(6591)
            .WithBindAddress("127.0.0.1")
            .Ephemeral().Build();

        server.Endpoint.Should().Be("127.0.0.1:6591");
    }

    [Fact]
    public void WithBindAddress_IPAddressOverload_EndpointReflectsAddress()
    {
        using var server = new HighwayServerBuilder()
            .WithPort(6592)
            .WithBindAddress(IPAddress.Loopback)
            .Ephemeral().Build();

        server.Endpoint.Should().Be("127.0.0.1:6592");
    }

    [Fact]
    public void WithBindAddress_InvalidString_RejectedAtBuild_NamingValue()
    {
        var builder = new HighwayServerBuilder().WithBindAddress("not-an-ip-address");

        var act = () => builder.Ephemeral().Build();

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not-an-ip-address*");
    }

    [Fact]
    public void Endpoint_DefaultRendersConfiguredAddress()
    {
        using var server = new HighwayServerBuilder().WithPort(6593).Ephemeral().Build();

        server.Endpoint.Should().Be("127.0.0.1:6593");
    }

    [Fact]
    public void WithOptions_AppliesDelegate()
    {
        using var server = new HighwayServerBuilder()
            .WithOptions(o => o.Port = 6594)
            .Ephemeral().Build();

        server.Endpoint.Should().Be("127.0.0.1:6594");
    }
}
