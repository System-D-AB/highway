using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Highway.Server.Host;

/// <summary>
/// Feature 045 — file logging for the packaged broker. A broker installed as a Windows service
/// has no console (its stdout is discarded by the Service Control Manager) and the shipped
/// <c>logs/</c> folder was never written to, so operators had no readable log. This writes
/// rolling daily files into that folder — plus the console, so an interactive run still streams.
///
/// <para>By convention there is <b>no configuration knob</b> (as with the cache directory): the
/// distribution lays out <c>bin/highways.exe</c> with a sibling <c>logs/</c>, and the exe's base
/// directory is <c>bin/</c>, so <c>&lt;baseDir&gt;/../logs</c> is that shipped folder regardless of
/// the process working directory (which, for a service, is <c>System32</c>).</para>
/// </summary>
internal static class HostLogging
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// The directory the broker writes log files to: the distribution's <c>logs/</c> folder, a
    /// sibling of the <c>bin/</c> the executable runs from. Resolved from the executable location,
    /// never the working directory, so it is correct under the Service Control Manager.
    /// </summary>
    public static string ResolveLogDirectory()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));

    /// <summary>
    /// Builds the Serilog logger that backs the host's <c>ILogger&lt;T&gt;</c> pipeline: console
    /// (for interactive runs) plus daily-rolling files in <paramref name="logDirectory"/>, capped
    /// by size and retention so the folder never grows without bound.
    /// </summary>
    public static Logger CreateLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.File(
                path: Path.Combine(logDirectory, "highway-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                fileSizeLimitBytes: 1L * 1024 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: OutputTemplate)
            .CreateLogger();
    }
}
