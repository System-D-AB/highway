using FluentAssertions;
using Xunit;

namespace Highway.Server.Host.Tests;

public class WindowsServiceVerbsTests
{
    [Fact]
    public void StatusOfNonExistentService_ReturnsSuccess_WithNotInstalledMessage()
    {
        var dummyName = "HighwayTest_" + Guid.NewGuid().ToString("N")[..8];
        var (exit, stdout, stderr) = RunWith("--status", "--service-name", dummyName);

        if (OperatingSystem.IsWindows())
        {
            exit.Should().Be(ExitCodes.Success);
            stdout.Should().Contain(dummyName);
            stdout.Should().Contain("not installed");
        }
        else
        {
            exit.Should().Be(ExitCodes.PlatformUnsupported);
        }
    }

    [Fact]
    public void StopOfNonExistentService_ReturnsConflictOrAccessDenied()
    {
        var dummyName = "HighwayTest_" + Guid.NewGuid().ToString("N")[..8];
        var (exit, _, stderr) = RunWith("--stop", "--service-name", dummyName);

        if (OperatingSystem.IsWindows())
        {
            if (WindowsServiceManager.IsAdministrator())
            {
                exit.Should().Be(ExitCodes.ServiceStateConflict);
                stderr.Should().Contain("not installed");
            }
            else
            {
                exit.Should().Be(ExitCodes.PrivilegeInsufficient);
                stderr.Should().Contain("Administrator");
            }
        }
        else
        {
            exit.Should().Be(ExitCodes.PlatformUnsupported);
        }
    }

    [Fact]
    public void StartOfNonExistentService_ReturnsConflictOrAccessDenied()
    {
        var dummyName = "HighwayTest_" + Guid.NewGuid().ToString("N")[..8];
        var (exit, _, stderr) = RunWith("--start", "--service-name", dummyName);

        if (OperatingSystem.IsWindows())
        {
            if (WindowsServiceManager.IsAdministrator())
            {
                exit.Should().Be(ExitCodes.ServiceStateConflict);
                stderr.Should().Contain("not installed");
            }
            else
            {
                exit.Should().Be(ExitCodes.PrivilegeInsufficient);
                stderr.Should().Contain("Administrator");
            }
        }
        else
        {
            exit.Should().Be(ExitCodes.PlatformUnsupported);
        }
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
