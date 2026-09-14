using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Highway.Client.Hosting;

/// <summary>
/// Implements Windows Service Control Manager (SCM) verbs via Win32 P/Invoke.
/// Originated in feature 031 for the broker; extracted and parameterized in
/// feature 036 so any app can use it through <c>HighwayHost</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsServiceManager
{
    #region Win32 Constants

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint DELETE = 0x00010000;

    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;

    private const uint SERVICE_CONTROL_STOP = 0x00000001;

    private const uint SERVICE_STOPPED = 0x00000001;
    private const uint SERVICE_START_PENDING = 0x00000002;
    private const uint SERVICE_STOP_PENDING = 0x00000003;
    private const uint SERVICE_RUNNING = 0x00000004;
    private const uint SERVICE_CONTINUE_PENDING = 0x00000005;
    private const uint SERVICE_PAUSE_PENDING = 0x00000006;
    private const uint SERVICE_PAUSED = 0x00000007;

    private const uint SERVICE_CONFIG_DESCRIPTION = 1;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;

    private const uint SC_ACTION_RESTART = 1;

    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    #endregion

    #region Win32 P/Invoke

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, IntPtr lpInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTION
    {
        public string lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public uint Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpRebootMsg;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    #endregion

    /// <summary>Returns true when the current process is elevated (Administrator).</summary>
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Dispatches a verb against the SCM using the resolved <paramref name="identity"/>.
    /// </summary>
    public static int Dispatch(
        string verb,
        ServiceIdentity identity,
        string[] passthroughArgs,
        bool startAfterInstall)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows service verbs are only supported on Windows.");
            return ExitCodes.PlatformUnsupported;
        }

        return verb switch
        {
            "install" => Install(identity, passthroughArgs, startAfterInstall),
            "uninstall" => Uninstall(identity.Name),
            "status" => Status(identity.Name),
            "start" => Start(identity.Name),
            "stop" => Stop(identity.Name),
            _ => ExitCodes.Unexpected
        };
    }

    private static int Install(ServiceIdentity identity, string[] passthroughArgs, bool startImmediately)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Run as Administrator to install Windows services.");
            return ExitCodes.PrivilegeInsufficient;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            exePath = Path.Combine(AppContext.BaseDirectory,
                Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]) + ".exe");
        }
        exePath = Path.GetFullPath(exePath);

        // Build binary path: the exe plus any passthrough args
        var binaryPath = $"\"{exePath}\"";
        if (passthroughArgs.Length > 0)
        {
            binaryPath += " " + string.Join(" ", passthroughArgs.Select(a =>
                a.Contains(' ') ? $"\"{a}\"" : a));
        }

        var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_ACCESS_DENIED)
            {
                Console.Error.WriteLine("Run as Administrator to install Windows services.");
                return ExitCodes.PrivilegeInsufficient;
            }
            Console.Error.WriteLine($"Failed to open Service Control Manager: {new Win32Exception(err).Message}");
            return ExitCodes.Unexpected;
        }

        try
        {
            var existingService = OpenService(scm, identity.Name, SERVICE_QUERY_CONFIG);
            if (existingService != IntPtr.Zero)
            {
                CloseServiceHandle(existingService);
                Console.Error.WriteLine($"Service '{identity.Name}' is already installed.");
                return ExitCodes.ServiceStateConflict;
            }

            var service = CreateService(
                scm,
                identity.Name,
                identity.DisplayName,
                SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                binaryPath,
                null,
                IntPtr.Zero,
                null,
                null,
                null);

            if (service == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"Failed to create service '{identity.Name}': {new Win32Exception(err).Message}");
                return err == ERROR_ACCESS_DENIED ? ExitCodes.PrivilegeInsufficient : ExitCodes.Unexpected;
            }

            try
            {
                SetDescription(service, identity.Description);
                SetFailureActions(service);

                Console.WriteLine($"Service '{identity.Name}' ({identity.DisplayName}) installed successfully.");
            }
            finally
            {
                CloseServiceHandle(service);
            }

            if (startImmediately)
            {
                return Start(identity.Name);
            }

            return ExitCodes.Success;
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void SetDescription(IntPtr service, string description)
    {
        var desc = new SERVICE_DESCRIPTION { lpDescription = description };
        var descPtr = Marshal.AllocHGlobal(Marshal.SizeOf(desc));
        try
        {
            Marshal.StructureToPtr(desc, descPtr, false);
            ChangeServiceConfig2(service, SERVICE_CONFIG_DESCRIPTION, descPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(descPtr);
        }
    }

    private static void SetFailureActions(IntPtr service)
    {
        // Restart after 5s, 30s, 60s; reset period 24h (86400s)
        var actions = new SC_ACTION[]
        {
            new() { Type = SC_ACTION_RESTART, Delay = 5000 },
            new() { Type = SC_ACTION_RESTART, Delay = 30000 },
            new() { Type = SC_ACTION_RESTART, Delay = 60000 }
        };

        var actionSize = Marshal.SizeOf(typeof(SC_ACTION));
        var actionsPtr = Marshal.AllocHGlobal(actionSize * actions.Length);
        try
        {
            for (var i = 0; i < actions.Length; i++)
            {
                var target = IntPtr.Add(actionsPtr, i * actionSize);
                Marshal.StructureToPtr(actions[i], target, false);
            }

            var failureActions = new SERVICE_FAILURE_ACTIONS
            {
                dwResetPeriod = 86400,
                cActions = (uint)actions.Length,
                lpsaActions = actionsPtr
            };

            var failurePtr = Marshal.AllocHGlobal(Marshal.SizeOf(failureActions));
            try
            {
                Marshal.StructureToPtr(failureActions, failurePtr, false);
                ChangeServiceConfig2(service, SERVICE_CONFIG_FAILURE_ACTIONS, failurePtr);
            }
            finally
            {
                Marshal.FreeHGlobal(failurePtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(actionsPtr);
        }
    }

    private static int Uninstall(string serviceName)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Run as Administrator to uninstall Windows services.");
            return ExitCodes.PrivilegeInsufficient;
        }

        var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_ACCESS_DENIED)
            {
                Console.Error.WriteLine("Run as Administrator to uninstall Windows services.");
                return ExitCodes.PrivilegeInsufficient;
            }
            Console.Error.WriteLine($"Failed to open Service Control Manager: {new Win32Exception(err).Message}");
            return ExitCodes.Unexpected;
        }

        try
        {
            var service = OpenService(scm, serviceName, SERVICE_STOP | SERVICE_QUERY_STATUS | DELETE);
            if (service == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Console.WriteLine($"Service '{serviceName}' is not installed.");
                    return ExitCodes.Success;
                }
                Console.Error.WriteLine($"Failed to open service '{serviceName}': {new Win32Exception(err).Message}");
                return err == ERROR_ACCESS_DENIED ? ExitCodes.PrivilegeInsufficient : ExitCodes.Unexpected;
            }

            try
            {
                // Stop service first if running
                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(service, ref status) && status.dwCurrentState != SERVICE_STOPPED)
                {
                    Console.WriteLine($"Stopping service '{serviceName}' before removal...");
                    ControlService(service, SERVICE_CONTROL_STOP, ref status);

                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (QueryServiceStatus(service, ref status) && status.dwCurrentState == SERVICE_STOPPED)
                            break;
                        Thread.Sleep(500);
                    }

                    if (status.dwCurrentState != SERVICE_STOPPED)
                    {
                        Console.Error.WriteLine($"Warning: Service '{serviceName}' did not stop within 30 seconds. Proceeding with deletion.");
                    }
                }

                if (!DeleteService(service))
                {
                    var err = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"Failed to delete service '{serviceName}': {new Win32Exception(err).Message}");
                    return ExitCodes.Unexpected;
                }

                Console.WriteLine($"Service '{serviceName}' uninstalled successfully.");
                return ExitCodes.Success;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    internal static int Status(string serviceName)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"Failed to open Service Control Manager: {new Win32Exception(err).Message}");
            return err == ERROR_ACCESS_DENIED ? ExitCodes.PrivilegeInsufficient : ExitCodes.Unexpected;
        }

        try
        {
            var service = OpenService(scm, serviceName, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Console.WriteLine($"Service '{serviceName}' is not installed.");
                    return ExitCodes.Success;
                }
                Console.Error.WriteLine($"Failed to query service '{serviceName}': {new Win32Exception(err).Message}");
                return ExitCodes.Unexpected;
            }

            try
            {
                var status = new SERVICE_STATUS();
                if (!QueryServiceStatus(service, ref status))
                {
                    var err = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"Failed to query service status: {new Win32Exception(err).Message}");
                    return ExitCodes.Unexpected;
                }

                var stateStr = status.dwCurrentState switch
                {
                    SERVICE_STOPPED => "Stopped",
                    SERVICE_START_PENDING => "StartPending",
                    SERVICE_STOP_PENDING => "StopPending",
                    SERVICE_RUNNING => "Running",
                    SERVICE_CONTINUE_PENDING => "ContinuePending",
                    SERVICE_PAUSE_PENDING => "PausePending",
                    SERVICE_PAUSED => "Paused",
                    _ => $"Unknown (0x{status.dwCurrentState:X})"
                };

                Console.WriteLine($"Service '{serviceName}': {stateStr}");
                return ExitCodes.Success;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    internal static int Start(string serviceName)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Run as Administrator to start Windows services.");
            return ExitCodes.PrivilegeInsufficient;
        }

        var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_ACCESS_DENIED)
            {
                Console.Error.WriteLine("Run as Administrator to start Windows services.");
                return ExitCodes.PrivilegeInsufficient;
            }
            Console.Error.WriteLine($"Failed to open Service Control Manager: {new Win32Exception(err).Message}");
            return ExitCodes.Unexpected;
        }

        try
        {
            var service = OpenService(scm, serviceName, SERVICE_START | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Console.Error.WriteLine($"Service '{serviceName}' is not installed.");
                    return ExitCodes.ServiceStateConflict;
                }
                Console.Error.WriteLine($"Failed to open service '{serviceName}': {new Win32Exception(err).Message}");
                return err == ERROR_ACCESS_DENIED ? ExitCodes.PrivilegeInsufficient : ExitCodes.Unexpected;
            }

            try
            {
                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(service, ref status) && status.dwCurrentState == SERVICE_RUNNING)
                {
                    Console.WriteLine($"Service '{serviceName}' is already running.");
                    return ExitCodes.Success;
                }

                if (!StartService(service, 0, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_SERVICE_ALREADY_RUNNING)
                    {
                        Console.WriteLine($"Service '{serviceName}' is already running.");
                        return ExitCodes.Success;
                    }
                    Console.Error.WriteLine($"Failed to start service '{serviceName}': {new Win32Exception(err).Message}");
                    return ExitCodes.Unexpected;
                }

                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    if (QueryServiceStatus(service, ref status) && status.dwCurrentState == SERVICE_RUNNING)
                    {
                        Console.WriteLine($"Service '{serviceName}' started.");
                        return ExitCodes.Success;
                    }
                    Thread.Sleep(500);
                }

                Console.WriteLine($"Service '{serviceName}' start command sent (current state: {status.dwCurrentState}).");
                return ExitCodes.Success;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    internal static int Stop(string serviceName)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Run as Administrator to stop Windows services.");
            return ExitCodes.PrivilegeInsufficient;
        }

        var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_ACCESS_DENIED)
            {
                Console.Error.WriteLine("Run as Administrator to stop Windows services.");
                return ExitCodes.PrivilegeInsufficient;
            }
            Console.Error.WriteLine($"Failed to open Service Control Manager: {new Win32Exception(err).Message}");
            return ExitCodes.Unexpected;
        }

        try
        {
            var service = OpenService(scm, serviceName, SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Console.Error.WriteLine($"Service '{serviceName}' is not installed.");
                    return ExitCodes.ServiceStateConflict;
                }
                Console.Error.WriteLine($"Failed to open service '{serviceName}': {new Win32Exception(err).Message}");
                return err == ERROR_ACCESS_DENIED ? ExitCodes.PrivilegeInsufficient : ExitCodes.Unexpected;
            }

            try
            {
                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(service, ref status) && status.dwCurrentState == SERVICE_STOPPED)
                {
                    Console.WriteLine($"Service '{serviceName}' is not running.");
                    return ExitCodes.Success;
                }

                if (!ControlService(service, SERVICE_CONTROL_STOP, ref status))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_SERVICE_NOT_ACTIVE)
                    {
                        Console.WriteLine($"Service '{serviceName}' is not running.");
                        return ExitCodes.Success;
                    }
                    Console.Error.WriteLine($"Failed to stop service '{serviceName}': {new Win32Exception(err).Message}");
                    return ExitCodes.Unexpected;
                }

                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    if (QueryServiceStatus(service, ref status) && status.dwCurrentState == SERVICE_STOPPED)
                    {
                        Console.WriteLine($"Service '{serviceName}' stopped.");
                        return ExitCodes.Success;
                    }
                    Thread.Sleep(500);
                }

                Console.WriteLine($"Service '{serviceName}' stop command sent (current state: {status.dwCurrentState}).");
                return ExitCodes.Success;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
