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
    public void Build_DefaultsToLoopback()
    {
        // Secure by default: with nothing configured the broker binds loopback on 6500.
        using var server = new HighwayServerBuilder().Ephemeral().Build();

        server.Endpoint.Should().Be($"{IPAddress.Loopback}:6500");
    }

    [Fact]
    public void Build_BindAddressMapsThrough()
    {
        using var server = new HighwayServerBuilder()
            .WithBindAddress(IPAddress.Any)
            .WithPort(6595)
            // Off loopback the bind-address rule (012) requires authentication; give it one so
            // Build() reaches the endpoint assertion rather than SecurityPolicy.Enforce.
            .WithPassword("s3cret")
            .Ephemeral().Build();

        server.Endpoint.Should().Be($"{IPAddress.Any}:6595");
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
