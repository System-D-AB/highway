using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Highway.Client;

/// <summary>
/// What hosts this process, when something names it (feature 072): the Windows service it runs as, else
/// the IIS application pool. Several copies of one application on one machine — three Windows services
/// built from the same binaries behind a load balancer — differ only here, so this is what makes the
/// default <see cref="HighwayOptions.NodeName"/> unique per process.
/// </summary>
internal static class ProcessHosting
{
    /// <summary>
    /// The hosting name, or null when nothing names this process. Never throws: a name that cannot be
    /// read (access denied, not Windows, API missing) is no name — detection must never stop a node starting.
    /// </summary>
    public static string? DetectHostingName()
    {
        try
        {
            if (OperatingSystem.IsWindows() && WindowsServiceName(Environment.ProcessId) is { Length: > 0 } service)
                return service;
            return Environment.GetEnvironmentVariable("APP_POOL_ID") is { Length: > 0 } pool ? pool : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Joins identifier parts with '-' into a valid node or group name: characters the server rejects (C0,
    /// DEL) and '@' (reserved for group-queue routing) become '_', and the result is cut to the identifier
    /// byte cap on a character boundary.
    /// </summary>
    public static string Compose(params string[] parts)
    {
        var joined = string.Join("-", parts.Where(p => !string.IsNullOrEmpty(p)));
        var sb = new StringBuilder(joined.Length);
        foreach (var c in joined)
            sb.Append(c < 0x20 || c == 0x7F || c == '@' ? '_' : c);

        var text = sb.ToString();
        if (Encoding.UTF8.GetByteCount(text) <= HighwayOptionsValidator.MaxNodeNameBytes) return text;

        var bytes = 0;
        var end = 0;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > HighwayOptionsValidator.MaxNodeNameBytes) break;
            bytes += size;
            end += element.Length;
        }
        return text[..end];
    }

    // ---- Windows: the service whose process is this one -------------------------------------------

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x00000030;
    private const uint SERVICE_ACTIVE = 0x00000001;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusEx(
        IntPtr scManager, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>One enumeration of the active Win32 services, matched on process id.</summary>
    private static string? WindowsServiceName(int processId)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return null;
        try
        {
            uint resume = 0;
            var size = 0u;
            var buffer = IntPtr.Zero;
            try
            {
                while (true)
                {
                    var ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                        buffer, size, out var needed, out var returned, ref resume, null);
                    var error = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && error != ERROR_MORE_DATA) throw new Win32Exception(error);

                    var stride = Marshal.SizeOf<EnumServiceStatusProcess>();
                    for (var i = 0; i < returned; i++)
                    {
                        var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * stride);
                        if (entry.ProcessId == (uint)processId)
                            return Marshal.PtrToStringUni(entry.ServiceName);
                    }
                    if (ok) return null;

                    // More data: grow to what the call asked for and continue from the resume handle.
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    size = Math.Max(needed, 4096u);
                    buffer = Marshal.AllocHGlobal((int)size);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
