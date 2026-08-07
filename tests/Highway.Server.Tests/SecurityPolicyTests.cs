using System.Net;
using FluentAssertions;
using Highway.Server;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 012, Requirement 1 — the bind-address rule: authentication is not required
/// on loopback and is required off it.
///
/// <para>The first test here is the one that matters most. Highway's zero-configuration
/// start is the path every newcomer meets, and a security feature is exactly the kind of
/// change that erodes it one reasonable-sounding step at a time.</para>
/// </summary>
public class SecurityPolicyTests
{
    /// <summary>
    /// <b>The line that must not move.</b> A bare builder produces a working, entirely
    /// unsecured broker on loopback — no credentials, no certificate, no extra call, no
    /// generated password to find in a log.
    ///
    /// <para>Do not delete this as a duplicate of a happy-path test elsewhere. Nothing
    /// else asserts that the <i>unconfigured</i> case survives, and that is the whole
    /// point of it.</para>
    /// </summary>
    [Fact]
    public void BareBuild_OnLoopback_RunsWithNoSecurity()
    {
        using var server = new HighwayServerBuilder()
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .Build();

        server.Should().NotBeNull();
        server.Start();   // must not throw: no password, no certificate, nothing
    }

    [Fact]
    public void NonLoopbackBind_WithoutAuthentication_ThrowsAtBuild_NamingBothRemedies()
    {
        var act = () => new HighwayServerBuilder()
            .WithBindAddress(IPAddress.Any)
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .Build();

        var ex = act.Should().Throw<InvalidOperationException>().Which;

        ex.Message.Should().Contain("0.0.0.0", "the message must name the bind address that triggered it");
        ex.Message.Should().Contain("WithPassword", "the secure remedy must be named");
        ex.Message.Should().Contain("WithoutAuthentication", "the explicit override must be named too, "
            + "so nobody goes looking for one and invents something worse");
    }

    [Fact]
    public void NonLoopbackBind_WithPassword_Builds()
    {
        using var server = new HighwayServerBuilder()
            .WithBindAddress(IPAddress.Any)
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .WithPassword("s3cret")
            .Build();

        server.Should().NotBeNull();
    }

    [Fact]
    public void NonLoopbackBind_WithoutAuthentication_Builds_AndWarns()
    {
        var log = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));

        using var server = new HighwayServerBuilder()
            .WithBindAddress(IPAddress.Any)
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .WithoutAuthentication()
            .WithLoggerFactory(factory)
            .Build();

        log.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("WITHOUT authentication"),
            "running open off loopback is supported, but it is not quiet");
    }

    /// <summary>
    /// The <b>level</b> is the design decision, not an incidental choice: warning on a
    /// configuration that is correct teaches people to filter the category, and then the
    /// warning that matters — the one above — is invisible too.
    /// </summary>
    [Fact]
    public void LoopbackWithoutAuthentication_LogsInformation_NotWarning()
    {
        var log = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));

        using var server = new HighwayServerBuilder()
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .WithLoggerFactory(factory)
            .Build();

        var line = log.Entries.Should().ContainSingle(e =>
            e.Message.Contains("running without authentication")).Subject;

        line.Level.Should().Be(LogLevel.Information);
        line.Message.Should().Contain("Binding to another address will require credentials",
            "the line should say what changes off loopback, so the Build() throw is not a surprise");

        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning,
            "an unauthenticated loopback server is a correct configuration, not a problem");
    }

    [Fact]
    public void BlankPassword_IsRejected_RatherThanSilentlyAccepted()
    {
        // An unset environment variable arriving as " " would otherwise produce a server
        // the operator believes is secured by something it is not.
        var act = () => new HighwayServerBuilder()
            .WithOptions(o => o.Authentication.Password = "   ")
            .Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty or whitespace*");
    }

    [Fact]
    public void PasswordAndSettingsTogether_AreRejected()
    {
        var act = () => new HighwayServerBuilder()
            .WithOptions(o =>
            {
                o.Authentication.Password = "s3cret";
                o.Authentication.Settings =
                    new Garnet.server.Auth.Settings.PasswordAuthenticationSettings("other");
            })
            .Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one*", "the password would be silently ignored otherwise");
    }

    [Fact]
    public void WithoutAuthentication_PlusAPassword_IsRejected()
    {
        var act = () => new HighwayServerBuilder()
            .WithPassword("s3cret")
            .WithoutAuthentication()
            .Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*opposite things*", "the outcome would otherwise depend on call order");
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.5", false)]
    [InlineData("::1", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("192.168.1.10", true)]
    public void RequiresAuthentication_DependsOnlyOnTheBindAddress(string address, bool required)
        => Security.SecurityPolicy.RequiresAuthentication(IPAddress.Parse(address))
            .Should().Be(required);
}
