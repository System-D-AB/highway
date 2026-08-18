using System.Collections;
using FluentAssertions;
using Highway.Server.Host.Configuration;
using Xunit;

namespace Highway.Server.Host.Tests;

/// <summary>
/// Feature 031 T2 — loading, precedence, unknown keys, path resolution, validation.
/// Every test passes an explicit environment dictionary so a machine's real
/// environment can never change an assertion.
/// </summary>
public class ConfigurationLoaderTests : IDisposable
{
    private static readonly Hashtable NoEnvironment = [];

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "highways-conf-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfigurationLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* locked file must not fail a passing test */ }
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "highway.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void NoFile_NoEnvironment_YieldsTheCodeDefaults()
    {
        var loaded = ConfigurationLoader.Load(null, NoEnvironment);

        loaded.SourcePath.Should().BeNull();
        loaded.Configuration.Server.Port.Should().Be(6500);
        loaded.Configuration.Server.BindAddress.Should().Be("127.0.0.1");
        loaded.Configuration.Server.AofSizeLimitBytes.Should().Be(512L * 1024 * 1024);
        loaded.Configuration.Server.Lease.Should().Be(TimeSpan.FromMinutes(5));
        loaded.Configuration.Authentication.Password.Should().BeNull();
        loaded.Configuration.Dashboard.Enabled.Should().BeFalse();
        loaded.Configuration.Dashboard.Port.Should().Be(7500);
    }

    [Fact]
    public void AFile_OverridesDefaults()
    {
        var path = WriteConfig("""
            {
              "server": { "port": 7777, "aofSizeLimitBytes": "256m", "lease": "00:02:00" },
              "authentication": { "password": "secret" },
              "dashboard": { "enabled": true, "port": 8500 }
            }
            """);

        var loaded = ConfigurationLoader.Load(path, NoEnvironment);

        loaded.SourcePath.Should().Be(Path.GetFullPath(path));
        loaded.Configuration.Server.Port.Should().Be(7777);
        loaded.Configuration.Server.AofSizeLimitBytes.Should().Be(256L * 1024 * 1024);
        loaded.Configuration.Server.Lease.Should().Be(TimeSpan.FromMinutes(2));
        loaded.Configuration.Authentication.Password.Should().Be("secret");
        loaded.Configuration.Dashboard.Enabled.Should().BeTrue();
        loaded.Configuration.Dashboard.Port.Should().Be(8500);
    }

    [Fact]
    public void Comments_AndTrailingCommas_ArePermitted()
    {
        var path = WriteConfig("""
            {
              // an operator's note
              "server": {
                "port": 6600,
              },
            }
            """);

        var loaded = ConfigurationLoader.Load(path, NoEnvironment);

        loaded.Configuration.Server.Port.Should().Be(6600);
    }

    [Fact]
    public void AnUnknownKey_IsRefused_NamingTheKey()
    {
        var path = WriteConfig("""{ "server": { "portt": 6600 } }""");

        var act = () => ConfigurationLoader.Load(path, NoEnvironment);

        act.Should().Throw<ConfigurationException>()
           .WithMessage("*portt*");
    }

    [Fact]
    public void Precedence_IsFileUnderEnvironmentUnderCommandLine()
    {
        var path = WriteConfig("""{ "server": { "port": 1111, "maxDeliveryAttempts": 3 } }""");

        var environment = new Hashtable
        {
            ["HIGHWAY_SERVER_PORT"] = "2222",
            ["HIGHWAY_SERVER_MAXDELIVERYATTEMPTS"] = "7",
        };

        // CLI beats environment beats file.
        var withCli = ConfigurationLoader.Load(path, environment, cliPort: 3333);
        withCli.Configuration.Server.Port.Should().Be(3333);
        withCli.Configuration.Server.MaxDeliveryAttempts.Should().Be(7, "the environment beats the file where the CLI is silent");

        // Environment beats file.
        var withoutCli = ConfigurationLoader.Load(path, environment);
        withoutCli.Configuration.Server.Port.Should().Be(2222);
        withoutCli.Configuration.Server.MaxDeliveryAttempts.Should().Be(7);

        // File alone.
        var fileOnly = ConfigurationLoader.Load(path, NoEnvironment);
        fileOnly.Configuration.Server.Port.Should().Be(1111);
        fileOnly.Configuration.Server.MaxDeliveryAttempts.Should().Be(3);
    }

    [Fact]
    public void ARelativeDataDir_FromTheFile_ResolvesAgainstTheFileDirectory()
    {
        var path = WriteConfig("""{ "server": { "dataDir": "../data" } }""");

        var loaded = ConfigurationLoader.Load(path, NoEnvironment);

        loaded.Configuration.Server.DataDir.Should().Be(
            Path.GetFullPath(Path.Combine(_dir, "..", "data")));
    }

    [Fact]
    public void ARelativeDataDir_FromTheEnvironment_ResolvesAgainstTheCurrentDirectory()
    {
        var path = WriteConfig("""{ "server": { "dataDir": "../data" } }""");
        var environment = new Hashtable { ["HIGHWAY_SERVER_DATADIR"] = "elsewhere" };

        var loaded = ConfigurationLoader.Load(path, environment);

        loaded.Configuration.Server.DataDir.Should().Be(
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "elsewhere")),
            "an environment value stands where the operator stands, not where the file lives");
    }

    [Fact]
    public void AnAbsoluteDataDir_IsHonoredVerbatim()
    {
        var absolute = Path.Combine(_dir, "absolute-store");
        var path = WriteConfig($$"""{ "server": { "dataDir": "{{absolute.Replace("\\", "\\\\")}}" } }""");

        var loaded = ConfigurationLoader.Load(path, NoEnvironment);

        loaded.Configuration.Server.DataDir.Should().Be(Path.GetFullPath(absolute));
    }

    [Fact]
    public void AnOutOfRangePort_IsRefused_NamingTheKey()
    {
        var path = WriteConfig("""{ "server": { "port": 99999 } }""");

        var act = () => ConfigurationLoader.Load(path, NoEnvironment);

        act.Should().Throw<ConfigurationException>().WithMessage("*server.port*");
    }

    [Fact]
    public void PasswordAndAclFile_Together_AreRefused()
    {
        var path = WriteConfig("""{ "authentication": { "password": "p", "aclFile": "users.acl" } }""");

        var act = () => ConfigurationLoader.Load(path, NoEnvironment);

        act.Should().Throw<ConfigurationException>()
           .WithMessage("*authentication.password*aclFile*");
    }

    [Fact]
    public void CertFileAndSubjectName_Together_AreRefused()
    {
        var path = WriteConfig("""{ "tls": { "certFile": "c.pfx", "certSubjectName": "CN=highway" } }""");

        var act = () => ConfigurationLoader.Load(path, NoEnvironment);

        act.Should().Throw<ConfigurationException>().WithMessage("*tls.certFile*certSubjectName*");
    }

    [Fact]
    public void EphemeralAndDataDir_Together_AreRefused()
    {
        var path = WriteConfig("""{ "server": { "ephemeral": true, "dataDir": "./data" } }""");

        var act = () => ConfigurationLoader.Load(path, NoEnvironment);

        act.Should().Throw<ConfigurationException>().WithMessage("*ephemeral*");
    }

    [Fact]
    public void AGarbageSize_IsRefused_NamingTheKey()
    {
        var path = WriteConfig("""{ "server": { "aofSizeLimitBytes": "lots" } }""");

        var act = () => ConfigurationLoader.Load(path, NoEnvironment);

        act.Should().Throw<ConfigurationException>().WithMessage("*aofSizeLimitBytes*");
    }

    [Fact]
    public void AMissingFile_IsRefused_NamingThePath()
    {
        var act = () => ConfigurationLoader.Load(Path.Combine(_dir, "absent.json"), NoEnvironment);

        act.Should().Throw<ConfigurationException>().WithMessage("*absent.json*");
    }

    [Fact]
    public void ObservabilityOverrides_BindFromTheFile()
    {
        var path = WriteConfig("""
            {
              "server": {
                "observability": {
                  "defaultCapture": "headersOnly",
                  "overrides": { "orders.placed": { "capture": "off", "capacity": 10 } }
                }
              }
            }
            """);

        var loaded = ConfigurationLoader.Load(path, NoEnvironment);

        loaded.Configuration.Server.Observability.DefaultCapture.Should()
            .Be(Highway.Abstractions.Observability.PayloadCapture.HeadersOnly);
        loaded.Configuration.Server.Observability.Overrides["orders.placed"].Capture.Should()
            .Be(Highway.Abstractions.Observability.PayloadCapture.Off);
        loaded.Configuration.Server.Observability.Overrides["orders.placed"].Capacity.Should().Be(10);
    }
}
