using FluentAssertions;
using Xunit;

namespace Highway.Client.Hosting.Tests;

/// <summary>
/// Tests <see cref="SystemdServiceManager"/> using a fake process runner,
/// so they run on any OS without root or systemd.
/// </summary>
public class SystemdServiceManagerTests
{
    // --- Install verb ---

    [Fact]
    public void Install_CallsDaemonReloadThenEnable()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var exit = mgr.Dispatch("install", identity, [], startAfterInstall: false);

        exit.Should().Be(ExitCodes.Success);

        runner.Invocations.Should().HaveCount(2);
        runner.Invocations[0].Args.Should().Equal("daemon-reload");
        runner.Invocations[1].Args.Should().Equal("enable", "myapp.service");
    }

    [Fact]
    public void Install_WithStart_CallsEnableNow()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var exit = mgr.Dispatch("install", identity, [], startAfterInstall: true);

        exit.Should().Be(ExitCodes.Success);
        runner.Invocations[1].Args.Should().Equal("enable", "--now", "myapp.service");
    }

    [Fact]
    public void Install_WritesUnitFile()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp", description: "My Application");

        mgr.Dispatch("install", identity, ["--port", "8080"], startAfterInstall: false);

        mgr.WrittenUnitContent.Should().NotBeNull();
        mgr.WrittenUnitContent.Should().Contain("Description=My Application");
        mgr.WrittenUnitContent.Should().Contain("Type=notify");
        mgr.WrittenUnitContent.Should().Contain("--port 8080");
    }

    // --- Uninstall verb ---

    [Fact]
    public void Uninstall_CallsDisableNowThenDaemonReload()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var exit = mgr.Dispatch("uninstall", identity, [], startAfterInstall: false);

        exit.Should().Be(ExitCodes.Success);

        runner.Invocations.Should().HaveCount(2);
        runner.Invocations[0].Args.Should().Equal("disable", "--now", "myapp.service");
        runner.Invocations[1].Args.Should().Equal("daemon-reload");
    }

    // --- Start verb ---

    [Fact]
    public void Start_CallsSystemctlStart()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var exit = mgr.Dispatch("start", identity, [], startAfterInstall: false);

        exit.Should().Be(ExitCodes.Success);
        runner.Invocations.Should().ContainSingle();
        runner.Invocations[0].Args.Should().Equal("start", "myapp.service");
    }

    [Fact]
    public void Start_NotInstalled_ReturnsServiceStateConflict()
    {
        var runner = new FakeProcessRunner();
        runner.NextResult = new ProcessResult(5, "", "Failed to start myapp.service: Unit not-found.\n");
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var exit = mgr.Dispatch("start", identity, [], startAfterInstall: false);

        exit.Should().Be(ExitCodes.ServiceStateConflict);
    }

    // --- Stop verb ---

    [Fact]
    public void Stop_CallsSystemctlStop()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var exit = mgr.Dispatch("stop", identity, [], startAfterInstall: false);

        exit.Should().Be(ExitCodes.Success);
        runner.Invocations.Should().ContainSingle();
        runner.Invocations[0].Args.Should().Equal("stop", "myapp.service");
    }

    // --- Status verb ---

    [Fact]
    public void Status_ReportsActiveState()
    {
        var runner = new FakeProcessRunner();
        runner.NextResult = new ProcessResult(0, "active\n", "");
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var (exit, stdout, _) = CaptureOutput(() =>
            mgr.Dispatch("status", identity, [], startAfterInstall: false));

        exit.Should().Be(ExitCodes.Success);
        stdout.Should().Contain("active");
        runner.Invocations[0].Args.Should().Equal("is-active", "myapp.service");
    }

    [Fact]
    public void Status_ReportsInactiveState()
    {
        var runner = new FakeProcessRunner();
        runner.NextResult = new ProcessResult(3, "inactive\n", "");
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var (exit, stdout, _) = CaptureOutput(() =>
            mgr.Dispatch("status", identity, [], startAfterInstall: false));

        exit.Should().Be(ExitCodes.Success);
        stdout.Should().Contain("inactive");
    }

    // --- Privilege and platform checks ---

    [Fact]
    public void NotRoot_ReturnsPrivilegeInsufficient()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: false, hasSystemd: true);
        var identity = MakeIdentity("myapp");

        var (exit, _, stderr) = CaptureOutput(() =>
            mgr.Dispatch("install", identity, [], startAfterInstall: false));

        exit.Should().Be(ExitCodes.PrivilegeInsufficient);
        stderr.Should().Contain("sudo");
    }

    [Fact]
    public void NoSystemd_ReturnsPlatformUnsupported()
    {
        var runner = new FakeProcessRunner();
        var mgr = new TestableSystemdServiceManager(runner, isRoot: true, hasSystemd: false);
        var identity = MakeIdentity("myapp");

        var (exit, _, stderr) = CaptureOutput(() =>
            mgr.Dispatch("install", identity, [], startAfterInstall: false));

        exit.Should().Be(ExitCodes.PlatformUnsupported);
        stderr.Should().Contain("systemd");
    }

    // --- Helpers ---

    private static ServiceIdentity MakeIdentity(
        string name, string? description = null)
    {
        var verbOptions = new VerbOptions
        {
            Name = name,
            Description = description ?? name,
        };
        return ServiceIdentity.Resolve(verbOptions, null).Identity!;
    }

    private static (int Exit, string StdOut, string StdErr) CaptureOutput(Func<int> action)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (action(), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    /// <summary>Fake process runner that records invocations.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<Invocation> Invocations { get; } = [];
        public ProcessResult NextResult { get; set; } = new(0, "", "");

        public ProcessResult Run(string fileName, string[] arguments)
        {
            Invocations.Add(new Invocation(fileName, arguments));
            return NextResult;
        }

        public record Invocation(string FileName, string[] Args);
    }

    /// <summary>
    /// Testable subclass that overrides root/systemd checks and captures
    /// the unit file content instead of writing to disk.
    /// </summary>
    private sealed class TestableSystemdServiceManager : SystemdServiceManager
    {
        private readonly bool _isRoot;
        private readonly bool _hasSystemd;

        public string? WrittenUnitContent { get; private set; }
        public string? WrittenUnitPath { get; private set; }

        public TestableSystemdServiceManager(
            IProcessRunner runner, bool isRoot, bool hasSystemd)
            : base(runner)
        {
            _isRoot = isRoot;
            _hasSystemd = hasSystemd;
        }

        internal override bool CheckIsRoot() => _isRoot;
        internal override bool CheckHasSystemd() => _hasSystemd;
        internal override void WriteUnitFile(string path, string content)
        {
            WrittenUnitPath = path;
            WrittenUnitContent = content;
        }
        internal override void DeleteUnitFile(string path) { /* no-op */ }
        internal override bool UnitFileExists(string path) => WrittenUnitContent is not null;
    }
}
