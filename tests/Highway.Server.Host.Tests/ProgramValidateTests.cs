using System.Collections;
using FluentAssertions;
using Xunit;

namespace Highway.Server.Host.Tests;

/// <summary>
/// Feature 031 R1.4 — <c>--validate</c> loads, validates, prints the effective
/// configuration masked, and exits without starting the server.
/// </summary>
public class ProgramValidateTests : IDisposable
{
    private static readonly Hashtable NoEnvironment = [];

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "highways-validate-" + Guid.NewGuid().ToString("N")[..8]);

    public ProgramValidateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a locked file must not fail a passing test */ }
    }

    private (int Exit, string StdOut, string StdErr) Run(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (Program.Run(args, NoEnvironment), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void AValidConfig_PrintsTheMaskedEffectiveConfiguration_AndExitsSuccess()
    {
        var path = Path.Combine(_dir, "highway.json");
        File.WriteAllText(path, """
            {
              "server": { "port": 7200 },
              "authentication": { "password": "hunter2" }
            }
            """);

        var (exit, stdout, _) = Run("--validate", "--config", path);

        exit.Should().Be(ExitCodes.Success);
        stdout.Should().Contain(path);
        stdout.Should().Contain("7200");
        stdout.Should().NotContain("hunter2", "a password on a console is a password in a screen-share");
        stdout.Should().Contain("********");
    }

    [Fact]
    public void AnInvalidConfig_ExitsConfigurationInvalid_NamingTheKey()
    {
        var path = Path.Combine(_dir, "highway.json");
        File.WriteAllText(path, """{ "server": { "port": 99999 } }""");

        var (exit, _, stderr) = Run("--validate", "--config", path);

        exit.Should().Be(ExitCodes.ConfigurationInvalid);
        stderr.Should().Contain("server.port");
    }

    [Fact]
    public void AnUnknownKey_ExitsConfigurationInvalid_NamingTheKey()
    {
        var path = Path.Combine(_dir, "highway.json");
        File.WriteAllText(path, """{ "server": { "portt": 6600 } }""");

        var (exit, _, stderr) = Run("--validate", "--config", path);

        exit.Should().Be(ExitCodes.ConfigurationInvalid);
        stderr.Should().Contain("portt");
    }

    [Fact]
    public void NoConfigFile_ValidatesTheDefaults()
    {
        var (exit, stdout, _) = Run("--validate");

        exit.Should().Be(ExitCodes.Success);
        stdout.Should().Contain("6500", "with no file, the code defaults are what an operator is validating");
    }
}
