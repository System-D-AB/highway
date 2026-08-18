namespace Highway.Server.Host;

/// <summary>
/// Process exit codes for <c>highways</c> (feature 031, design § Exit Codes). Each
/// failure class has a distinct code so scripts and service managers can branch on
/// the outcome without parsing text — success is 0, and every non-zero value names
/// exactly one cause.
/// </summary>
public static class ExitCodes
{
    /// <summary>Ran and stopped cleanly; a verb succeeded; <c>--validate</c> passed.</summary>
    public const int Success = 0;

    /// <summary>Unexpected failure — nothing more specific applies.</summary>
    public const int Unexpected = 1;

    /// <summary>Configuration invalid; the message names the key.</summary>
    public const int ConfigurationInvalid = 2;

    /// <summary>Data directory unusable or incompatible (storage format, permissions).</summary>
    public const int DataDirectoryUnusable = 3;

    /// <summary>Privilege insufficient for a service verb.</summary>
    public const int PrivilegeInsufficient = 4;

    /// <summary>Service state conflict (install over existing, uninstall of absent…).</summary>
    public const int ServiceStateConflict = 5;

    /// <summary>Platform unsupported for the verb (no SCM, no systemd).</summary>
    public const int PlatformUnsupported = 6;
}
