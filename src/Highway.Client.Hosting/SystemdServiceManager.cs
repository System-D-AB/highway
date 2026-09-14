namespace Highway.Client.Hosting;

/// <summary>
/// Implements systemd service verbs on Linux: writes/removes unit files and
/// drives <c>systemctl</c> via an injectable <see cref="IProcessRunner"/>.
/// Root and systemd checks are virtual for testability.
/// </summary>
internal class SystemdServiceManager
{
    private const string UnitDir = "/etc/systemd/system";
    private const string Systemctl = "systemctl";

    private readonly IProcessRunner _runner;

    public SystemdServiceManager(IProcessRunner? runner = null)
    {
        _runner = runner ?? new DefaultProcessRunner();
    }

    /// <summary>
    /// Dispatches a verb using the resolved <paramref name="identity"/>.
    /// </summary>
    public int Dispatch(
        string verb,
        ServiceIdentity identity,
        string[] passthroughArgs,
        bool startAfterInstall)
    {
        if (!CheckIsRoot())
        {
            Console.Error.WriteLine("Run with sudo to manage systemd services.");
            return ExitCodes.PrivilegeInsufficient;
        }

        if (!CheckHasSystemd())
        {
            Console.Error.WriteLine("systemd is not available on this machine.");
            return ExitCodes.PlatformUnsupported;
        }

        return verb switch
        {
            "install" => Install(identity, passthroughArgs, startAfterInstall),
            "uninstall" => Uninstall(identity.Name),
            "start" => Start(identity.Name),
            "stop" => Stop(identity.Name),
            "status" => Status(identity.Name),
            _ => ExitCodes.Unexpected
        };
    }

    private int Install(ServiceIdentity identity, string[] passthroughArgs, bool startAfterInstall)
    {
        var execStart = SystemdUnit.DetectExecStart();
        var workDir = AppContext.BaseDirectory.TrimEnd('/');

        var unitContent = SystemdUnit.Render(identity, execStart, workDir, passthroughArgs);
        var unitPath = UnitPath(identity.Name);

        try
        {
            WriteUnitFile(unitPath, unitContent);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot write to {unitPath} — run with sudo.");
            return ExitCodes.PrivilegeInsufficient;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Failed to write unit file: {ex.Message}");
            return ExitCodes.Unexpected;
        }

        // daemon-reload
        var reload = _runner.Run(Systemctl, ["daemon-reload"]);
        if (reload.ExitCode != 0)
        {
            Console.Error.WriteLine($"systemctl daemon-reload failed: {reload.StdErr.Trim()}");
            return ExitCodes.Unexpected;
        }

        // enable (or enable --now)
        var enableArgs = startAfterInstall
            ? new[] { "enable", "--now", UnitName(identity.Name) }
            : new[] { "enable", UnitName(identity.Name) };

        var enable = _runner.Run(Systemctl, enableArgs);
        if (enable.ExitCode != 0)
        {
            Console.Error.WriteLine($"systemctl enable failed: {enable.StdErr.Trim()}");
            return ExitCodes.Unexpected;
        }

        var msg = startAfterInstall
            ? $"Service '{identity.Name}' installed and started."
            : $"Service '{identity.Name}' installed.";
        Console.WriteLine(msg);
        return ExitCodes.Success;
    }

    private int Uninstall(string name)
    {
        // disable --now (stops and removes from boot)
        _runner.Run(Systemctl, ["disable", "--now", UnitName(name)]);
        // Non-zero is ok if the service wasn't enabled — we still try to clean up

        var unitPath = UnitPath(name);
        if (UnitFileExists(unitPath))
        {
            try
            {
                DeleteUnitFile(unitPath);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Failed to delete unit file: {ex.Message}");
                return ExitCodes.Unexpected;
            }
        }

        // daemon-reload
        var reload = _runner.Run(Systemctl, ["daemon-reload"]);
        if (reload.ExitCode != 0)
        {
            Console.Error.WriteLine($"systemctl daemon-reload failed: {reload.StdErr.Trim()}");
            return ExitCodes.Unexpected;
        }

        Console.WriteLine($"Service '{name}' uninstalled.");
        return ExitCodes.Success;
    }

    private int Start(string name)
    {
        var result = _runner.Run(Systemctl, ["start", UnitName(name)]);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"Failed to start service '{name}': {result.StdErr.Trim()}");
            return IsNotInstalled(result.StdErr) ? ExitCodes.ServiceStateConflict : ExitCodes.Unexpected;
        }

        Console.WriteLine($"Service '{name}' started.");
        return ExitCodes.Success;
    }

    private int Stop(string name)
    {
        var result = _runner.Run(Systemctl, ["stop", UnitName(name)]);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"Failed to stop service '{name}': {result.StdErr.Trim()}");
            return IsNotInstalled(result.StdErr) ? ExitCodes.ServiceStateConflict : ExitCodes.Unexpected;
        }

        Console.WriteLine($"Service '{name}' stopped.");
        return ExitCodes.Success;
    }

    private int Status(string name)
    {
        // systemctl is-active returns 0 for active, non-zero for inactive/failed/not-found
        var isActive = _runner.Run(Systemctl, ["is-active", UnitName(name)]);
        var state = isActive.StdOut.Trim();

        if (string.IsNullOrEmpty(state))
            state = "unknown";

        Console.WriteLine($"Service '{name}': {state}");
        return ExitCodes.Success;
    }

    private static string UnitName(string name) => $"{name}.service";
    private static string UnitPath(string name) => Path.Combine(UnitDir, UnitName(name));

    /// <summary>Detects "not-found" or "not loaded" in systemctl stderr.</summary>
    private static bool IsNotInstalled(string stderr)
        => stderr.Contains("not-found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("not loaded", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    // --- Virtual hooks for testability ---

    internal virtual bool CheckIsRoot()
    {
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            var runner = new DefaultProcessRunner();
            var result = runner.Run("id", ["-u"]);
            return result.StdOut.Trim() == "0";
        }
        catch
        {
            return false;
        }
    }

    internal virtual bool CheckHasSystemd()
        => Directory.Exists("/run/systemd/system");

    internal virtual void WriteUnitFile(string path, string content)
        => File.WriteAllText(path, content);

    internal virtual void DeleteUnitFile(string path)
        => File.Delete(path);

    internal virtual bool UnitFileExists(string path)
        => File.Exists(path);
}
