using System.Globalization;
using System.Text;

namespace Highway.Client.Hosting;

/// <summary>
/// Renders a deterministic systemd unit file. Output is byte-identical for the same
/// inputs: ordered keys, <c>\n</c> line endings, invariant culture — so golden-file
/// tests run on any OS.
/// </summary>
internal static class SystemdUnit
{
    /// <summary>
    /// Renders the unit file content for a <c>Type=notify</c> service.
    /// </summary>
    /// <param name="identity">Service identity (name, display name, description).</param>
    /// <param name="execStart">Absolute path to the executable, including any arguments.</param>
    /// <param name="workingDirectory">Absolute path to the working directory.</param>
    /// <param name="passthroughArgs">Additional arguments appended to <c>ExecStart</c>.</param>
    public static string Render(
        ServiceIdentity identity,
        string execStart,
        string workingDirectory,
        string[] passthroughArgs)
    {
        var sb = new StringBuilder();

        // [Unit]
        sb.Append("[Unit]\n");
        sb.Append(CultureInfo.InvariantCulture, $"Description={identity.Description}\n");
        sb.Append("After=network.target\n");
        sb.Append('\n');

        // [Service]
        sb.Append("[Service]\n");
        sb.Append("Type=notify\n");

        var fullExecStart = passthroughArgs.Length > 0
            ? execStart + " " + string.Join(" ", passthroughArgs.Select(a =>
                a.Contains(' ') ? $"\"{a}\"" : a))
            : execStart;

        sb.Append(CultureInfo.InvariantCulture, $"ExecStart={fullExecStart}\n");
        sb.Append(CultureInfo.InvariantCulture, $"WorkingDirectory={workingDirectory}\n");
        sb.Append("Restart=on-failure\n");
        sb.Append("RestartSec=5\n");

        if (identity.User is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"User={identity.User}\n");
        }

        sb.Append('\n');

        // [Install]
        sb.Append("[Install]\n");
        sb.Append("WantedBy=multi-user.target\n");

        return sb.ToString();
    }

    /// <summary>
    /// Detects whether the current process is a framework-dependent app
    /// (<c>dotnet app.dll</c>) or a self-contained apphost.
    /// </summary>
    /// <returns>
    /// The absolute <c>ExecStart</c> value: either <c>/path/to/app</c> for an apphost
    /// or <c>/usr/bin/dotnet /path/to/app.dll</c> for framework-dependent.
    /// </returns>
    public static string DetectExecStart()
        => DetectExecStart(
            Environment.ProcessPath,
            System.Reflection.Assembly.GetEntryAssembly()?.Location);

    /// <summary>
    /// Pure core of <see cref="DetectExecStart()"/>, separated for tests.
    /// systemd rejects a non-absolute <c>ExecStart</c>, so the framework-dependent
    /// form must use the absolute path of the dotnet host — which is exactly what
    /// <paramref name="processPath"/> is when the app was launched as
    /// <c>dotnet app.dll</c>.
    /// </summary>
    internal static string DetectExecStart(string? processPath, string? entryDllPath)
    {
        var isDotnetHost = !string.IsNullOrEmpty(processPath)
            && (processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                || processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase));

        // Self-contained apphost: the process path is the executable itself
        if (!string.IsNullOrEmpty(processPath)
            && !isDotnetHost
            && !processPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(processPath);
        }

        // Framework-dependent: {absolute dotnet} <app>.dll
        if (!string.IsNullOrEmpty(entryDllPath))
        {
            var dotnet = isDotnetHost
                ? Path.GetFullPath(processPath!)
                : "/usr/bin/dotnet"; // last resort — the conventional install path

            return $"{dotnet} {Path.GetFullPath(entryDllPath)}";
        }

        // Fallback: best-effort from ProcessPath
        return Path.GetFullPath(processPath ?? "app");
    }
}
