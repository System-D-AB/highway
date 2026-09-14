namespace Highway.Client.Hosting;

/// <summary>
/// Process exit codes shared by every app using <see cref="HighwayHost"/>
/// (originated in feature 031, moved here in feature 036). Values are preserved —
/// scripts and service managers rely on these numbers.
/// </summary>
/// <remarks>
/// Mapping from 031 originals:
/// <list type="bullet">
///   <item><c>Success = 0</c> — unchanged</item>
///   <item><c>Unexpected = 1</c> — unchanged</item>
///   <item><c>ConfigurationInvalid = 2</c> — unchanged</item>
///   <item><c>DataDirectoryUnusable = 3</c> — unchanged</item>
///   <item><c>PrivilegeInsufficient = 4</c> — unchanged</item>
///   <item><c>ServiceStateConflict = 5</c> — unchanged</item>
///   <item><c>PlatformUnsupported = 6</c> — unchanged</item>
///   <item><c>InvalidArguments = 7</c> — new in 036 (verb option errors)</item>
/// </list>
/// </remarks>
public static class ExitCodes
{
    /// <summary>Ran and stopped cleanly; a verb succeeded.</summary>
    public const int Success = 0;

    /// <summary>Unexpected failure — nothing more specific applies.</summary>
    public const int Unexpected = 1;

    /// <summary>Configuration invalid; the message names the key.</summary>
    public const int ConfigurationInvalid = 2;

    /// <summary>Data directory unusable or incompatible (storage format, permissions).</summary>
    public const int DataDirectoryUnusable = 3;

    /// <summary>Privilege insufficient for a service verb (not elevated / not root).</summary>
    public const int PrivilegeInsufficient = 4;

    /// <summary>Service state conflict (install over existing, uninstall of absent…).</summary>
    public const int ServiceStateConflict = 5;

    /// <summary>Platform unsupported for the verb (no SCM, no systemd).</summary>
    public const int PlatformUnsupported = 6;

    /// <summary>Invalid verb arguments (unknown option, missing value).</summary>
    public const int InvalidArguments = 7;
}
