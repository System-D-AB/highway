using SharedExitCodes = Highway.Client.Hosting.ExitCodes;

namespace Highway.Server.Host;

/// <summary>
/// Process exit codes for <c>highways</c>. Feature 036 extracted the shared register
/// to <see cref="Highway.Client.Hosting.ExitCodes"/>; this class preserves the local
/// namespace so existing code and tests compile unchanged.
/// </summary>
public static class ExitCodes
{
    /// <summary>Ran and stopped cleanly; a verb succeeded; <c>--validate</c> passed.</summary>
    public const int Success = SharedExitCodes.Success;

    /// <summary>Unexpected failure — nothing more specific applies.</summary>
    public const int Unexpected = SharedExitCodes.Unexpected;

    /// <summary>Configuration invalid; the message names the key.</summary>
    public const int ConfigurationInvalid = SharedExitCodes.ConfigurationInvalid;

    /// <summary>Data directory unusable or incompatible (storage format, permissions).</summary>
    public const int DataDirectoryUnusable = SharedExitCodes.DataDirectoryUnusable;

    /// <summary>Privilege insufficient for a service verb.</summary>
    public const int PrivilegeInsufficient = SharedExitCodes.PrivilegeInsufficient;

    /// <summary>Service state conflict (install over existing, uninstall of absent…).</summary>
    public const int ServiceStateConflict = SharedExitCodes.ServiceStateConflict;

    /// <summary>Platform unsupported for the verb (no SCM, no systemd).</summary>
    public const int PlatformUnsupported = SharedExitCodes.PlatformUnsupported;
}
