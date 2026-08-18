using FluentAssertions;
using Highway.Server.Host.Configuration;
using Xunit;

namespace Highway.Server.Host.Tests;

public class EffectiveConfigurationPrinterTests
{
    private static string Print(HostConfiguration configuration)
    {
        var writer = new StringWriter();
        EffectiveConfigurationPrinter.Print(configuration, writer);
        return writer.ToString();
    }

    [Fact]
    public void Secrets_AreMasked_NeverPrinted()
    {
        var configuration = new HostConfiguration();
        configuration.Authentication.Password = "hunter2";
        configuration.Tls.CertPassword = "pfx-secret";
        configuration.Dashboard.ApiKey = "key-123";

        var output = Print(configuration);

        output.Should().NotContain("hunter2");
        output.Should().NotContain("pfx-secret");
        output.Should().NotContain("key-123");
        output.Should().Contain("********");
    }

    [Fact]
    public void UnsetSecrets_PrintAsNotSet()
    {
        var output = Print(new HostConfiguration());

        output.Should().Contain("password                         : (not set)");
        output.Should().Contain("apiKey                           : (not set)");
    }

    [Fact]
    public void OrdinaryValues_ArePrinted()
    {
        var configuration = new HostConfiguration();
        configuration.Server.Port = 7777;
        configuration.Server.DataDir = "/var/lib/highway";

        var output = Print(configuration);

        output.Should().Contain("7777");
        output.Should().Contain("/var/lib/highway");
    }
}
