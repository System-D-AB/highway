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
/// <para><b>One password, no file.</b> A single password is matched against the
/// <c>default</c> user by the RESP server's <c>AUTH</c>. Verified behaviour: a wrong
/// password and absent credentials are refused.</para>
///
/// <para><b>The username is <c>default</c> unless named users are configured.</b> Clients
/// may send the password alone or pair it with the username <c>default</c>; both work.
/// For named users, populate <see cref="Users"/>.</para>
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
    /// Config users with hashed passwords (feature 040 / 037 R11). Each carries a name and a
    /// <see cref="PasswordHash"/> string (<c>PBKDF2$iterations$salt$key</c>); mint one with the
    /// documented recipe. When non-empty this is the credential directory the RESP server's
    /// <c>AUTH</c> verifies against — either <c>AUTH &lt;password&gt;</c> (matched against the
    /// <c>default</c> user) or <c>AUTH &lt;name&gt; &lt;password&gt;</c>.
    ///
    /// <para>When both this and <see cref="Password"/> are set, the users list takes
    /// precedence for the RESP server.</para>
    /// </summary>
    public IList<HighwayUser> Users { get; } = [];

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
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Password) || Users.Count > 0;

    /// <summary>
    /// Validates the configuration, naming the offending value. Called from
    /// <see cref="HighwayServerBuilder.Build"/>.
    /// </summary>
    public void Validate()
    {
        // A whitespace-only password is almost certainly a configuration accident —
        // an unset environment variable that arrived as " ". Accepting it would leave
        // the operator believing the server is secured by something it is not.
        if (Password is not null && string.IsNullOrWhiteSpace(Password))
            throw new InvalidOperationException(
                "AuthenticationOptions.Password was set but is empty or whitespace. " +
                "Supply a real password, or leave it unset to run without authentication.");

        if (ExplicitlyDisabled && IsConfigured)
            throw new InvalidOperationException(
                "WithoutAuthentication() was called on a server that also has authentication " +
                "configured. Remove one: the two say opposite things and the result would " +
                "depend on call order.");

        foreach (var user in Users)
        {
            if (string.IsNullOrWhiteSpace(user.Name))
                throw new InvalidOperationException("A configured user has a blank name.");
            if (!PasswordHash.IsHash(user.PasswordHash))
                throw new InvalidOperationException(
                    $"User '{user.Name}' has a password that is not a PBKDF2 hash string " +
                    $"('{PasswordHash.Scheme}$iterations$salt$key'). Hash it with the documented recipe; " +
                    "never store a plaintext password in the users list.");
        }
    }
}

/// <summary>
/// A config user (040 / 037 R11): a name and a <see cref="PasswordHash"/> string. The RESP server's
/// <c>AUTH</c> verifies a supplied password against the matching user's hash in constant time.
/// </summary>
public sealed record HighwayUser(string Name, string PasswordHash);
