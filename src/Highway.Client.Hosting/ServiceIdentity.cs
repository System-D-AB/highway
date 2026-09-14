using System.Reflection;
using System.Text.RegularExpressions;

namespace Highway.Client.Hosting;

/// <summary>
/// Resolves the service name, display name, and description used by the
/// <c>install</c> verb. Precedence: verb option → code option → convention.
/// Validation runs before any system call.
/// </summary>
public sealed partial class ServiceIdentity
{
    /// <summary>Service name used as the SCM key / systemd unit name.</summary>
    public string Name { get; }

    /// <summary>Display name shown in the SCM UI / systemd description.</summary>
    public string DisplayName { get; }

    /// <summary>Longer description (SCM description / systemd Description=).</summary>
    public string Description { get; }

    /// <summary>Linux only: the user to run the service as (omitted if null).</summary>
    public string? User { get; }

    private ServiceIdentity(string name, string displayName, string description, string? user)
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
        User = user;
    }

    /// <summary>
    /// Resolves identity with the three-tier precedence chain:
    /// verb option (from <paramref name="parsed"/>) → code option (from
    /// <paramref name="codeOptions"/>) → convention (entry assembly metadata).
    /// </summary>
    /// <param name="parsed">Options from the command line (may be null for each field).</param>
    /// <param name="codeOptions">Options set in code via <see cref="HostingOptions"/> (may be null).</param>
    /// <returns>A validated identity, or a failure message.</returns>
    public static Result Resolve(VerbOptions parsed, HostingOptions? codeOptions)
    {
        var conventionName = ConventionName();
        var conventionDescription = ConventionDescription();

        // Precedence: verb option > code option > convention
        var name = parsed.Name
                   ?? codeOptions?.ServiceName
                   ?? conventionName;

        var displayName = parsed.DisplayName
                          ?? codeOptions?.DisplayName
                          ?? name;

        var description = parsed.Description
                          ?? codeOptions?.Description
                          ?? conventionDescription
                          ?? displayName;

        var user = parsed.User ?? codeOptions?.User;

        // Validate
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Service name cannot be empty.");

        if (name.Length > 256)
            return Result.Failure($"Service name '{name}' exceeds 256 characters.");

        if (!ValidServiceName().IsMatch(name))
            return Result.Failure(
                $"Service name '{name}' contains invalid characters. " +
                "Use letters, digits, hyphens, underscores, and dots only.");

        if (user is not null && string.IsNullOrWhiteSpace(user))
            return Result.Failure("User name cannot be empty when specified.");

        return Result.Ok(new ServiceIdentity(name, displayName, description, user));
    }

    private static string ConventionName()
    {
        var entry = Assembly.GetEntryAssembly();
        return entry?.GetName().Name ?? "myapp";
    }

    private static string? ConventionDescription()
    {
        var entry = Assembly.GetEntryAssembly();
        return entry?.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
    }

    /// <summary>
    /// Letters, digits, hyphens, underscores, dots — the intersection of what Windows
    /// SCM and systemd unit names accept. No spaces, no slashes, no control chars.
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z0-9._\-]+$")]
    private static partial Regex ValidServiceName();

    /// <summary>Result of <see cref="Resolve"/>: either a valid identity or an error message.</summary>
    public readonly struct Result
    {
        /// <summary>The resolved identity, or null on failure.</summary>
        public ServiceIdentity? Identity { get; }

        /// <summary>The error message, or null on success.</summary>
        public string? Error { get; }

        /// <summary>True when resolution succeeded.</summary>
        public bool IsOk => Identity is not null;

        private Result(ServiceIdentity? identity, string? error)
        {
            Identity = identity;
            Error = error;
        }

        internal static Result Ok(ServiceIdentity identity) => new(identity, null);
        internal static Result Failure(string error) => new(null, error);
    }
}

/// <summary>
/// Code-level hosting options set in <c>Program.cs</c> — the middle tier of the
/// identity precedence chain (verb option → code option → convention).
/// </summary>
public sealed class HostingOptions
{
    /// <summary>Service name (SCM key / systemd unit name).</summary>
    public string? ServiceName { get; set; }

    /// <summary>Display name shown in the SCM UI.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Longer service description.</summary>
    public string? Description { get; set; }

    /// <summary>Linux: user to run as. Ignored on Windows.</summary>
    public string? User { get; set; }

    /// <summary>How long graceful shutdown waits before hard-killing. Default: 30 seconds.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
