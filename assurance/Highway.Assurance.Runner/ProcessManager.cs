namespace Highway.Assurance.Runner;

using System.Diagnostics;
using System.Text.Json;

public sealed class ManagedProcess : IAsyncDisposable, IDisposable
{
    public string Name { get; }
    public Process Process { get; }
    public string LogFile { get; }
    public long PeakWorkingSet { get; private set; }

    private readonly StreamWriter _logWriter;
    private bool _disposed;

    public ManagedProcess(string name, Process process, string logFile, StreamWriter logWriter)
    {
        Name = name;
        Process = process;
        LogFile = logFile;
        _logWriter = logWriter;
    }

    public void RefreshPeakMemory()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Refresh();
                var ws = Process.WorkingSet64;
                if (ws > PeakWorkingSet) PeakWorkingSet = ws;
            }
        }
        catch { }
    }

    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        RefreshPeakMemory();
        if (Process.HasExited) return;

        try
        {
            if (Process.CloseMainWindow())
            {
                using var cts = new CancellationTokenSource(timeout);
                await Process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            else
            {
                Process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await Process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (!Process.HasExited)
            {
                try { Process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    public void KillUngracefully()
    {
        RefreshPeakMemory();
        if (!Process.HasExited)
        {
            try { Process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RefreshPeakMemory();
        _logWriter.Dispose();
        Process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        RefreshPeakMemory();
        await _logWriter.DisposeAsync().ConfigureAwait(false);
        Process.Dispose();
    }
}

public sealed class ProcessManager : IAsyncDisposable, IDisposable
{
    private readonly string _runDir;
    private readonly string _dotnetPath;
    private readonly Dictionary<string, ManagedProcess> _processes = new(StringComparer.OrdinalIgnoreCase);

    public ProcessManager(string runDir)
    {
        _runDir = runDir;
        _dotnetPath = "dotnet";
        var procDir = Path.Combine(runDir, "processes");
        if (!Directory.Exists(procDir)) Directory.CreateDirectory(procDir);
    }

    public ManagedProcess StartDotnetAssembly(string name, string dllOrExePath, string arguments)
    {
        var logFile = Path.Combine(_runDir, "processes", $"{name}.stdout.log");
        var logStream = new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var logWriter = new StreamWriter(logStream) { AutoFlush = true };

        var isDll = dllOrExePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(dllOrExePath)) ?? AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = isDll ? _dotnetPath : dllOrExePath,
            Arguments = isDll ? $"exec \"{dllOrExePath}\" {arguments}" : arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = targetDir
        };

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) logWriter.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) logWriter.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] [ERR] {e.Data}"); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var managed = new ManagedProcess(name, process, logFile, logWriter);
        _processes[name] = managed;
        return managed;
    }

    public ManagedProcess? GetProcess(string name)
    {
        _processes.TryGetValue(name, out var p);
        return p;
    }

    public void RecordPeakResources(string outputFile)
    {
        var resources = new Dictionary<string, object>();
        foreach (var (name, proc) in _processes)
        {
            proc.RefreshPeakMemory();
            resources[name] = new
            {
                peakWorkingSetBytes = proc.PeakWorkingSet,
                peakWorkingSetMb = (proc.PeakWorkingSet / (1024.0 * 1024.0)).ToString("0.00") + " MB",
                exitCode = proc.Process.HasExited ? proc.Process.ExitCode : (int?)null
            };
        }

        var dir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputFile, JsonSerializer.Serialize(resources, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task StopAllGracefullyAsync(TimeSpan timeout)
    {
        await Task.WhenAll(_processes.Values.Select(p => p.StopGracefullyAsync(timeout))).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var p in _processes.Values) p.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _processes.Values) await p.DisposeAsync().ConfigureAwait(false);
    }
}
