using FluentAssertions;
using Xunit;

// Several classes in this project capture Console.Out/Error, which is process-global:
// parallel collections would trample each other's captures. The suite is fast; serial is fine.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Highway.Server.Host.Tests;

/// <summary>
/// Feature 031 T1 — verb dispatch, exit codes and <c>--version</c>.
/// </summary>
public class ProgramTests
{
    [Fact]
    public void Version_PrintsVersionStorageFormatAndRid_ThenExitsSuccess()
    {
        var (exit, stdout, _) = RunWith("--version");

        exit.Should().Be(ExitCodes.Success);
        stdout.Should().Contain("highways");
        stdout.Should().Contain($"storage format : {HighwayServerBuilder.StorageFormatVersion}");
        stdout.Should().Contain("runtime");
    }

    [Fact]
    public void UnknownArgument_ExitsNonZero_AndNamesTheKnownOnes()
    {
        var (exit, _, stderr) = RunWith("--frobnicate");

        exit.Should().NotBe(ExitCodes.Success);
        stderr.Should().Contain("Unknown argument '--frobnicate'");
        stderr.Should().Contain("--version", "the known arguments are listed so a typo is one read away from a fix");
    }

    [Theory]
    [InlineData("--status")]
    public void ServiceVerbs_StatusQuery_ExecutesCleanly(string verb)
    {
        var (exit, stdout, _) = RunWith(verb, "--service-name", "NonExistentServiceForTest" + Guid.NewGuid().ToString("N")[..8]);

        exit.Should().Be(ExitCodes.Success);
        stdout.Should().Contain("is not installed");
    }

    private static (int Exit, string StdOut, string StdErr) RunWith(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (Program.Run(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
