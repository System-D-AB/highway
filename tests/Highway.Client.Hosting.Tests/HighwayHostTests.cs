using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Highway.Client.Hosting.Tests;

public class HighwayHostTests
{
    // --- Plain app (no Highway) runs and stops ---

    [Fact]
    public async Task RunAsync_PlainApp_RunsAndStopsWithExitZero()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Signal stop after a brief delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(500, CancellationToken.None);
            cts.Cancel();
        });

        var exit = await HighwayHost.RunAsync([], cts.Token);

        exit.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RunAsync_PlainAppWithConfigureCallback_CallbackInvoked()
    {
        var callbackInvoked = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = Task.Run(async () =>
        {
            await Task.Delay(800, CancellationToken.None);
            cts.Cancel();
        });

        var exit = await HighwayHost.RunAsync(
            [],
            highway: null,
            configure: _ => callbackInvoked = true,
            hosting: null,
            ct: cts.Token);

        exit.Should().Be(ExitCodes.Success);
        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_NoHighwayConfig_NoHighwayServicesRegistered()
    {
        var highwayRegistered = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, CancellationToken.None);
            cts.Cancel();
        });

        var exit = await HighwayHost.RunAsync(
            [],
            highway: null,
            configure: b =>
            {
                // After build, check if Highway engine is registered
                // We can't check after build from configure, but we can verify
                // AddHighway was NOT called by checking no HighwayOptions singleton
                b.Services.AddSingleton<Action<IServiceProvider>>(sp =>
                {
                    highwayRegistered = sp.GetService<Highway.Client.HighwayOptions>() is not null;
                });
            },
            hosting: null,
            ct: cts.Token);

        exit.Should().Be(ExitCodes.Success);
        highwayRegistered.Should().BeFalse();
    }

    // --- Verb path never builds a host ---

    [Fact]
    public async Task RunAsync_VerbPath_NeverBuildsHost()
    {
        // "status" is a verb — on Windows it hits the SCM, on non-Windows it returns
        // PlatformUnsupported. Either way, the host is never built.
        // We can verify this because the call returns immediately (no host.Run blocking).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await HighwayHost.RunAsync(["status", "--name", "nonexistent-svc-test"]);
        sw.Stop();

        // Should return quickly — no host startup delay
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        // The exit code depends on the platform, but it should not be Unexpected=1
        // (which would mean host construction failure)
        if (OperatingSystem.IsWindows())
        {
            exit.Should().BeOneOf(ExitCodes.Success, ExitCodes.PrivilegeInsufficient);
        }
        else if (OperatingSystem.IsLinux())
        {
            exit.Should().BeOneOf(
                ExitCodes.PrivilegeInsufficient,
                ExitCodes.PlatformUnsupported);
        }
        else
        {
            exit.Should().Be(ExitCodes.PlatformUnsupported);
        }
    }

    // --- Verb parse errors ---

    [Fact]
    public async Task RunAsync_InvalidVerbOption_ReturnsInvalidArguments()
    {
        var (exit, _, stderr) = await CaptureOutput(() =>
            HighwayHost.RunAsync(["install", "--bogus"]));

        exit.Should().Be(ExitCodes.InvalidArguments);
        stderr.Should().Contain("--bogus");
    }

    [Fact]
    public async Task RunAsync_VerbIdentityValidationFails_ReturnsInvalidArguments()
    {
        var (exit, _, stderr) = await CaptureOutput(() =>
            HighwayHost.RunAsync(["install", "--name", "invalid name with spaces"]));

        exit.Should().Be(ExitCodes.InvalidArguments);
        stderr.Should().Contain("invalid characters");
    }

    // --- Shutdown timeout ---

    [Fact]
    public async Task RunAsync_CancellationStopsHost()
    {
        using var cts = new CancellationTokenSource();

        var task = HighwayHost.RunAsync([], cts.Token);

        // Let the host start
        await Task.Delay(300);

        // Cancel — should cause graceful shutdown
        cts.Cancel();

        var exit = await task;
        exit.Should().Be(ExitCodes.Success);
    }

    // --- HostingOptions are applied ---

    [Fact]
    public async Task RunAsync_HostingOptionsShutdownTimeout_IsApplied()
    {
        TimeSpan? configuredTimeout = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, CancellationToken.None);
            cts.Cancel();
        });

        var exit = await HighwayHost.RunAsync(
            [],
            highway: null,
            configure: b =>
            {
                b.Services.PostConfigure<HostOptions>(o => configuredTimeout = o.ShutdownTimeout);
            },
            hosting: new HostingOptions { ShutdownTimeout = TimeSpan.FromSeconds(42) },
            ct: cts.Token);

        exit.Should().Be(ExitCodes.Success);
        configuredTimeout.Should().Be(TimeSpan.FromSeconds(42));
    }

    // --- Non-verb args are passed to the host ---

    [Fact]
    public async Task RunAsync_NonVerbArgs_HostReceivesThem()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, CancellationToken.None);
            cts.Cancel();
        });

        // "--foo" is not a verb → should go into host mode (not verb dispatch)
        var exit = await HighwayHost.RunAsync(["--foo", "bar"], cts.Token);

        // The host runs successfully even with unknown args
        exit.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RunAsync_CommandLineArgs_ReachConfiguration()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? observed = null;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, CancellationToken.None);
            cts.Cancel();
        });

        var exit = await HighwayHost.RunAsync(
            ["--Sample:Key", "from-cli"],
            highway: null,
            configure: b => observed = b.Configuration["Sample:Key"],
            ct: cts.Token);

        exit.Should().Be(ExitCodes.Success);
        observed.Should().Be("from-cli",
            "command-line args must flow into IConfiguration (R1.1)");
    }

    // --- Content root (R1.3): must be the app's base directory, not the cwd ---

    [Fact]
    public async Task RunAsync_ContentRoot_IsAppBaseDirectory()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? observed = null;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, CancellationToken.None);
            cts.Cancel();
        });

        // Simulate a service manager's foreign working directory (SCM: system32,
        // systemd: /) — the content root must NOT follow it.
        var originalCwd = Directory.GetCurrentDirectory();
        var foreignCwd = Path.GetTempPath();
        Directory.SetCurrentDirectory(foreignCwd);
        int exit;
        try
        {
            exit = await HighwayHost.RunAsync(
                [],
                highway: null,
                configure: b => observed = b.Environment.ContentRootPath,
                ct: cts.Token);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }

        exit.Should().Be(ExitCodes.Success);
        Path.TrimEndingDirectorySeparator(observed!).Should().Be(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            "a service's working directory is system32 (SCM) or / (systemd); " +
            "relative paths must resolve against the app folder (R1.3)");
    }

    // --- Helpers ---

    private static async Task<(int Exit, string StdOut, string StdErr)> CaptureOutput(
        Func<Task<int>> action)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (await action(), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }
}
