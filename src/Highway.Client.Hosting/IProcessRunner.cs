using System.Diagnostics;

namespace Highway.Client.Hosting;

/// <summary>
/// Abstraction over process execution so <see cref="SystemdServiceManager"/>
/// can be unit-tested with a fake runner on any OS.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs a process and returns <c>(exitCode, stdout, stderr)</c>.
    /// </summary>
    ProcessResult Run(string fileName, string[] arguments);
}

/// <summary>The result of a process execution.</summary>
internal readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Real implementation: launches a process and captures output.</summary>
internal sealed class DefaultProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
