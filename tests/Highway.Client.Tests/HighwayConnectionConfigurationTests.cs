namespace Highway.Client.Tests;

using System.Security.Authentication;
using FluentAssertions;
using Highway.Client.Engine;
using StackExchange.Redis;
using Xunit;

public class HighwayConnectionConfigurationTests
{
    [Fact]
    public void Build_WithBasicConnectionString_ParsesEndpointsAndDefaults()
    {
        var options = HighwayConnectionConfiguration.Build("127.0.0.1:6379", null);

        options.EndPoints.Should().ContainSingle();
        options.EndPoints[0].ToString().Should().Be("127.0.0.1:6379");
        options.AbortOnConnectFail.Should().BeTrue();
    }

    [Fact]
    public void Build_NullOrWhitespaceConnectionString_ThrowsArgumentException()
    {
        var actNull = () => HighwayConnectionConfiguration.Build(null!, null);
        var actEmpty = () => HighwayConnectionConfiguration.Build("", null);

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_Precedence_SettingsOverrideConnectionString()
    {
        var settings = new HighwayOptions
        {
            Server = "127.0.0.1:6379,user=olduser,password=oldpass",
            Username = "newuser",
            Password = "newpassword"
        };

        var options = HighwayConnectionConfiguration.Build(settings.Server, settings);

        options.User.Should().Be("newuser");
        options.Password.Should().Be("newpassword");
    }

    [Fact]
    public void Build_Precedence_ConfigureConnectionDelegateRunsLast()
    {
        var settings = new HighwayOptions
        {
            Server = "127.0.0.1:6379",
            Username = "user1",
            Password = "pass1",
            ConfigureConnection = opt =>
            {
                opt.User = "overridden_user";
                opt.Password = "overridden_pass";
                opt.KeepAlive = 42;
            }
        };

        var options = HighwayConnectionConfiguration.Build(settings.Server, settings);

        options.User.Should().Be("overridden_user");
        options.Password.Should().Be("overridden_pass");
        options.KeepAlive.Should().Be(42);
    }

    [Fact]
    public void Build_TlsOptions_ConfiguresSslAndProtocols()
    {
        var settings = new HighwayOptions
        {
            Server = "127.0.0.1:6379",
            Tls = new HighwayTlsOptions
            {
                Enabled = true,
                TargetHost = "broker.highway.internal",
                Protocols = SslProtocols.Tls13
            }
        };

        var options = HighwayConnectionConfiguration.Build(settings.Server, settings);

        options.Ssl.Should().BeTrue();
        options.SslHost.Should().Be("broker.highway.internal");
        options.SslProtocols.Should().Be(SslProtocols.Tls13);
    }

    [Fact]
    public void Build_FromConnectionSettings_AppliesUsernamePasswordAndTls()
    {
        // Any IHighwayConnectionSettings implementation flows user/password/TLS into the
        // SE.Redis config. (Was exercised via the now-removed HighwayCacheOptions; HighwayOptions
        // is the surviving settings implementation and proves the same Build behaviour.)
        var settings = new HighwayOptions
        {
            Server = "127.0.0.1:6379",
            Username = "svc_user",
            Password = "svc_secret",
            Tls = new HighwayTlsOptions
            {
                Enabled = true,
                TargetHost = "cache.highway.internal"
            }
        };

        var options = HighwayConnectionConfiguration.Build(settings.Server!, settings);

        options.User.Should().Be("svc_user");
        options.Password.Should().Be("svc_secret");
        options.Ssl.Should().BeTrue();
        options.SslHost.Should().Be("cache.highway.internal");
    }
}
