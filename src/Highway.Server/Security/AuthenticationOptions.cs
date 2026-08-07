using Garnet.server.Auth.Settings;

namespace Highway.Server.Security;

/// <summary>
/// Authentication for the Highway server (feature 012).
///
/// <para><b>Optional, and free on loopback.</b> A server bound to loopback with no
/// password starts and runs exactly as it always has — that is the correct
/// configuration for development and evaluation. A server bound to any other
/// address requires either a password or an explicit
/// <c>WithoutAuthentication()</c>; see <see cref="HighwayServerBuilder.WithPassword"/>.</para>
///
/// <para><b>One password, no file.</b> Highway maps a password onto Garnet's
/// <see cref="AclAuthenticationPasswordSettings"/> with a <see langword="null"/>
/// ACL configuration file, which creates exactly one user — Garnet's <c>default</c>
/// — carrying that password. Verified behaviour: a wrong password, absent
/// credentials, and an unrecognised username are all refused.</para>
///
/// <para><b>Why ACL mode rather than <see cref="PasswordAuthenticationSettings"/>.</b>
/// Both are one call with no file and both refuse a wrong password. ACL mode
/// additionally rejects an unrecognised username, answers <c>ACL WHOAMI</c> for
/// diagnostics, and is the same authenticator that named users would need later —
/// so adding them is additive rather than a mode change.</para>
///
/// <para><b>The username is <c>default</c> and cannot be changed here.</b> Without an
/// ACL configuration file Garnet supports exactly one user. Highway therefore
/// promises a password, not a username directory. Clients may send the password
/// alone or pair it with the username <c>default</c>; both work. Anything else is
/// refused. Use <see cref="Settings"/> if you need named users.</para>
/// </summary>
public sealed class AuthenticationOptions
{
    /// <summary>
    /// The password every client must present. <see langword="null"/> or empty means
    /// no password-based authentication is configured.
    ///
    /// <para><b>Sent in clear text unless TLS is enabled.</b> RESP <c>AUTH</c> carries
    /// the password as an ordinary bulk string, so on an untrusted network configure
    /// <see cref="HighwayServerBuilder.WithTls(string, string)"/> as well. TLS is
    /// never required — Highway cannot invent a certificate — but a password crossing
    /// a network without one is a password on the wire.</para>
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Escape hatch: a fully-formed Garnet authenticator, used verbatim in place of
    /// anything Highway would construct. This is how ACL configuration files, named
    /// users, per-command rules and Entra ID remain reachable without waiting for
    /// Highway to wrap them.
    ///
    /// <para>Takes precedence over <see cref="Password"/>.</para>
    ///
    /// <para><b>Highway cannot reason about what you supply</b>, so for the purposes of
    /// the bind-address rule a non-null value counts as "authentication configured"
    /// and the server will start on any address. Two traps are worth knowing, both
    /// established by measurement rather than inference:</para>
    ///
    /// <list type="bullet">
    ///   <item><description><b>A <c>nopass</c> default user disables authentication entirely.</b>
    ///     With <c>user default on nopass</c> in an ACL file, a connection presenting a
    ///     nonexistent username and a wrong password is silently authenticated <i>as</i>
    ///     <c>default</c>, inheriting its permissions. Every other rule in the file
    ///     becomes decorative.</description></item>
    ///   <item><description><b>Highway's commands are in Garnet's <c>@dangerous</c> category</b>,
    ///     not <c>@admin</c>. A rule set of <c>+@all -@dangerous</c> — a common hardening
    ///     idiom — connects successfully and then refuses every <c>HW.*</c> command with
    ///     <c>NOPERM</c>. Grant <c>+@custom</c>, or name the commands individually.</description></item>
    /// </list>
    ///
    /// <para>An ACL file naming <c>hw.*</c> commands additionally needs
    /// <c>GarnetServerOptions.AclStrictCustomCommands = false</c>: Garnet validates those
    /// names while constructing the server, and Highway registers its commands after
    /// construction (required for AOF replay), so strict mode refuses to start.</para>
    /// </summary>
    public IAuthenticationSettings? Settings { get; set; }

    /// <summary>
    /// Set by <see cref="HighwayServerBuilder.WithoutAuthentication"/>. Running open is
    /// a supported configuration; it just has to be said out loud when the server is
    /// reachable from off the machine.
    /// </summary>
    internal bool ExplicitlyDisabled { get; set; }

    /// <summary>
    /// Whether this server will authenticate its clients. Drives the bind-address rule
    /// and the startup log line.
    /// </summary>
    public bool IsConfigured => Settings is not null || !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Builds the Garnet authenticator, or <see langword="null"/> when this server runs
    /// without authentication.
    /// </summary>
    internal IAuthenticationSettings? CreateSettings()
    {
        if (Settings is not null)
            return Settings;

        return string.IsNullOrWhiteSpace(Password)
            ? null
            : new AclAuthenticationPasswordSettings(aclConfigurationFile: null, defaultPassword: Password);
    }

    /// <summary>
    /// Validates the configuration, naming the offending value. Called from
    /// <see cref="HighwayServerBuilder.Build"/>.
    /// </summary>
    public void Validate()
    {
        // A whitespace-only password is almost certainly a configuration accident —
        // an unset environment variable that arrived as " ". Garnet would accept it
        // and the operator would believe the server is secured by something it is not.
        if (Password is not null && string.IsNullOrWhiteSpace(Password))
            throw new InvalidOperationException(
                "AuthenticationOptions.Password was set but is empty or whitespace. " +
                "Supply a real password, or leave it unset to run without authentication.");

        if (Settings is not null && !string.IsNullOrWhiteSpace(Password))
            throw new InvalidOperationException(
                "AuthenticationOptions specifies both Password and Settings. " +
                "Settings replaces everything Highway would construct, so the password would " +
                "be silently ignored — set exactly one.");

        if (ExplicitlyDisabled && IsConfigured)
            throw new InvalidOperationException(
                "WithoutAuthentication() was called on a server that also has authentication " +
                "configured. Remove one: the two say opposite things and the result would " +
                "depend on call order.");
    }
}
