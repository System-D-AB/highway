using FluentAssertions;
using Xunit;

namespace Highway.Client.Hosting.Tests;

public class SystemdUnitTests
{
    // --- DetectExecStart: systemd requires an ABSOLUTE ExecStart (R2.3) ---

    [Fact]
    public void DetectExecStart_Apphost_ReturnsExecutablePath()
    {
        var exe = Path.Combine(Path.GetTempPath(), "myapp.exe");

        var result = SystemdUnit.DetectExecStart(exe, entryDllPath: null);

        result.Should().Be(Path.GetFullPath(exe));
    }

    [Fact]
    public void DetectExecStart_FrameworkDependent_UsesAbsoluteDotnetPath()
    {
        // When launched as `dotnet app.dll`, ProcessPath IS the absolute dotnet host.
        var dotnet = Path.Combine(Path.GetTempPath(), "dotnet.exe");
        var dll = Path.Combine(Path.GetTempPath(), "myapp.dll");

        var result = SystemdUnit.DetectExecStart(dotnet, dll);

        result.Should().Be($"{Path.GetFullPath(dotnet)} {Path.GetFullPath(dll)}");
        result.Should().NotStartWith("dotnet ",
            "systemd rejects a non-absolute ExecStart");
    }

    [Fact]
    public void DetectExecStart_FrameworkDependent_LinuxStyleDotnet()
    {
        var dotnet = Path.Combine(Path.GetTempPath(), "dotnet"); // no .exe suffix
        var dll = Path.Combine(Path.GetTempPath(), "myapp.dll");

        var result = SystemdUnit.DetectExecStart(dotnet, dll);

        result.Should().Be($"{Path.GetFullPath(dotnet)} {Path.GetFullPath(dll)}");
    }

    [Fact]
    public void DetectExecStart_NoProcessPath_FallsBackToConventionalDotnet()
    {
        var dll = Path.Combine(Path.GetTempPath(), "myapp.dll");

        var result = SystemdUnit.DetectExecStart(processPath: null, dll);

        result.Should().Be($"/usr/bin/dotnet {Path.GetFullPath(dll)}");
    }

    [Fact]
    public void Render_Minimal_MatchesGoldenFile()
    {
        var identity = MakeIdentity("myapp", "myapp", "My Application");
        var result = SystemdUnit.Render(identity, "/opt/myapp/myapp", "/opt/myapp", []);

        result.Should().Be("""
            [Unit]
            Description=My Application
            After=network.target

            [Service]
            Type=notify
            ExecStart=/opt/myapp/myapp
            WorkingDirectory=/opt/myapp
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=multi-user.target

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Render_WithUser_IncludesUserLine()
    {
        var identity = MakeIdentity("myapp", "myapp", "My Application", user: "www-data");
        var result = SystemdUnit.Render(identity, "/opt/myapp/myapp", "/opt/myapp", []);

        result.Should().Be("""
            [Unit]
            Description=My Application
            After=network.target

            [Service]
            Type=notify
            ExecStart=/opt/myapp/myapp
            WorkingDirectory=/opt/myapp
            Restart=on-failure
            RestartSec=5
            User=www-data

            [Install]
            WantedBy=multi-user.target

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Render_WithPassthroughArgs_AppendsToExecStart()
    {
        var identity = MakeIdentity("myapp", "myapp", "My Application");
        var result = SystemdUnit.Render(
            identity,
            "/opt/myapp/myapp",
            "/opt/myapp",
            ["--config", "/etc/myapp/config.json", "--port", "9999"]);

        result.Should().Contain("ExecStart=/opt/myapp/myapp --config /etc/myapp/config.json --port 9999\n");
    }

    [Fact]
    public void Render_WithPassthroughArgs_QuotesSpaces()
    {
        var identity = MakeIdentity("myapp", "myapp", "My Application");
        var result = SystemdUnit.Render(
            identity,
            "/opt/myapp/myapp",
            "/opt/myapp",
            ["--name", "My Service"]);

        result.Should().Contain("ExecStart=/opt/myapp/myapp --name \"My Service\"\n");
    }

    [Fact]
    public void Render_DllBasedApp_DotnetPrefix()
    {
        var identity = MakeIdentity("myapp", "myapp", "My Application");
        var result = SystemdUnit.Render(
            identity,
            "dotnet /opt/myapp/myapp.dll",
            "/opt/myapp",
            []);

        result.Should().Contain("ExecStart=dotnet /opt/myapp/myapp.dll\n");
    }

    [Fact]
    public void Render_UsesLfLineEndings()
    {
        var identity = MakeIdentity("myapp", "myapp", "My Application");
        var result = SystemdUnit.Render(identity, "/opt/myapp/myapp", "/opt/myapp", []);

        result.Should().NotContain("\r\n");
        result.Should().Contain("\n");
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        var identity = MakeIdentity("myapp", "myapp", "My Application", user: "svc");

        var first = SystemdUnit.Render(identity, "/opt/myapp/myapp", "/opt/myapp", ["--port", "8080"]);
        var second = SystemdUnit.Render(identity, "/opt/myapp/myapp", "/opt/myapp", ["--port", "8080"]);

        first.Should().Be(second);
    }

    /// <summary>
    /// Helper to create a ServiceIdentity with known values, bypassing
    /// the convention/assembly resolution.
    /// </summary>
    private static ServiceIdentity MakeIdentity(
        string name, string displayName, string description, string? user = null)
    {
        var verbOptions = new VerbOptions
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            User = user,
        };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        return result.Identity!;
    }
}
