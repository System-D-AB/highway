using FluentAssertions;
using Highway.Server.Host;
using Serilog;
using Xunit;

namespace Highway.Server.Host.Tests;

/// <summary>
/// Feature 045 — the packaged broker writes readable log files. Before this, a broker installed
/// as a Windows service logged only to a console the SCM discards, leaving the shipped logs/
/// folder empty. These pin the two load-bearing facts: the log directory resolves from the
/// executable location (correct under the service, whose working directory is System32), and the
/// logger actually writes a rolling file.
/// </summary>
public class HostLoggingTests
{
    [Fact]
    public void ResolveLogDirectory_IsRooted_AndIsTheLogsFolder()
    {
        var dir = HostLogging.ResolveLogDirectory();

        Path.IsPathRooted(dir).Should().BeTrue("a service's working directory is System32 — the path must be absolute");
        Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)).Should().Be("logs");
    }

    [Fact]
    public void CreateLogger_WritesARollingFile_WithTheMessage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hwlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var logger = HostLogging.CreateLogger(dir))
            {
                logger.Information("broker started on port {Port}", 6500);
            } // disposing flushes the file sink

            var files = Directory.GetFiles(dir, "highway-*.log");
            files.Should().NotBeEmpty("a rolling log file must be created in the log directory");
            File.ReadAllText(files[0]).Should().Contain("broker started on port 6500");
        }
        finally
        {
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
