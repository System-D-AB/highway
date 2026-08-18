using System.Collections;
using FluentAssertions;
using Highway.Server.Host.Configuration;
using Xunit;

namespace Highway.Server.Host.Tests;

public class EnvironmentOverridesTests
{
    private static HostConfiguration Apply(IDictionary environment)
    {
        var configuration = new HostConfiguration();
        EnvironmentOverrides.Apply(configuration, environment);
        return configuration;
    }

    [Fact]
    public void RecognizedVariables_SetTheirValues()
    {
        var configuration = Apply(new Hashtable
        {
            ["HIGHWAY_SERVER_PORT"] = "7000",
            ["HIGHWAY_SERVER_AOFSIZELIMITBYTES"] = "1g",
            ["HIGHWAY_SERVER_EPHEMERAL"] = "true",
            ["HIGHWAY_SERVER_LEASE"] = "00:01:30",
            ["HIGHWAY_SERVER_OBSERVABILITY_DEFAULTCAPACITY"] = "42",
            ["HIGHWAY_SERVER_OBSERVABILITY_DEFAULTCAPTURE"] = "headersOnly",
            ["HIGHWAY_TLS_REVOCATIONMODE"] = "Online",
        });

        configuration.Server.Port.Should().Be(7000);
        configuration.Server.AofSizeLimitBytes.Should().Be(1024L * 1024 * 1024);
        configuration.Server.Ephemeral.Should().BeTrue();
        configuration.Server.Lease.Should().Be(TimeSpan.FromSeconds(90));
        configuration.Server.Observability.DefaultCapacity.Should().Be(42);
        configuration.Server.Observability.DefaultCapture.Should()
            .Be(Highway.Abstractions.Observability.PayloadCapture.HeadersOnly);
        configuration.Tls.RevocationMode.Should()
            .Be(System.Security.Cryptography.X509Certificates.X509RevocationMode.Online);
    }

    [Fact]
    public void TheAliasShortForms_Work()
    {
        var configuration = Apply(new Hashtable
        {
            ["HIGHWAY_PASSWORD"] = "secret",
            ["HIGHWAY_ACL_FILE"] = "/etc/highway/users.acl",
        });

        configuration.Authentication.Password.Should().Be("secret");
        configuration.Authentication.AclFile.Should().Be("/etc/highway/users.acl");
    }

    [Fact]
    public void AnUnknownHighwayVariable_IsIgnored_NotAnError()
    {
        // The process environment is shared space (the samples use their own HIGHWAY_*
        // names); a hard failure would break innocent shells. The JSON file is the
        // strict surface, not the environment.
        var act = () => Apply(new Hashtable { ["HIGHWAY_SOMETHING_ELSE"] = "x" });

        act.Should().NotThrow();
    }

    [Fact]
    public void NonHighwayVariables_AreNeverTouched()
    {
        var configuration = Apply(new Hashtable { ["PATH"] = "whatever", ["HOME"] = "x" });

        configuration.Server.Port.Should().Be(6500, "nothing outside HIGHWAY_* may change anything");
    }

    [Fact]
    public void AGarbageNumber_IsRefused_NamingTheVariable()
    {
        var act = () => Apply(new Hashtable { ["HIGHWAY_SERVER_PORT"] = "abc" });

        act.Should().Throw<ConfigurationException>().WithMessage("*HIGHWAY_SERVER_PORT*");
    }

    [Fact]
    public void ABareNumberDuration_IsRefused_BecauseItWouldSilentlyMeanDays()
    {
        var act = () => Apply(new Hashtable { ["HIGHWAY_SERVER_LEASE"] = "5" });

        act.Should().Throw<ConfigurationException>().WithMessage("*hh:mm:ss*");
    }

    [Fact]
    public void AnUnknownEnumValue_IsRefused_NamingTheVariable()
    {
        var act = () => Apply(new Hashtable { ["HIGHWAY_SERVER_OBSERVABILITY_DEFAULTCAPTURE"] = "bogus" });

        act.Should().Throw<ConfigurationException>().WithMessage("*HIGHWAY_SERVER_OBSERVABILITY_DEFAULTCAPTURE*");
    }

    [Fact]
    public void TouchedPaths_AreRecorded_ForPathResolution()
    {
        var configuration = new HostConfiguration();
        var touched = new HashSet<string>();

        EnvironmentOverrides.Apply(configuration, new Hashtable
        {
            ["HIGHWAY_SERVER_DATADIR"] = "elsewhere",
            ["HIGHWAY_SERVER_PORT"] = "7000",
        }, touched);

        touched.Should().BeEquivalentTo(["server.dataDir", "server.port"]);
    }
}
