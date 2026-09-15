using System.Net;
using System.Security.Cryptography;
using System.Text;
using Highway.Server.Security;

namespace Highway.Server.Resp;

/// <summary>
/// The connection authenticator (040 T6 / 037 R11 complete). It resolves the two exemptions 012
/// established — no authentication configured, or a loopback client — and verifies an <c>AUTH</c>
/// against either the config <see cref="AuthenticationOptions.Users"/> (PBKDF2 hashes, the R11
/// model) or the legacy single <see cref="AuthenticationOptions.Password"/> (plaintext, the
/// <c>default</c> user — how the Garnet-era option worked, kept for compatibility until it is
/// migrated).
///
/// <para>Both <c>AUTH</c> forms are honoured: <c>AUTH &lt;password&gt;</c> matches the
/// <c>default</c> user, <c>AUTH &lt;name&gt; &lt;password&gt;</c> a named one. Every compare is
/// constant-time — <see cref="PasswordHash.Verify"/> uses <see cref="CryptographicOperations.FixedTimeEquals"/>,
/// and the plaintext fallback does too — and a failure is permanent and legible on the wire
/// (<c>-WRONGPASS</c>, emitted by the session), never a lockout.</para>
/// </summary>
internal sealed class PasswordAuthenticator : IConnectionAuthenticator
{
    private readonly IReadOnlyDictionary<string, string> _userHashes; // name → PBKDF2 hash
    private readonly byte[]? _legacyDefaultPassword;                  // plaintext default user, if any
    private readonly bool _authDisabled;
    private readonly bool _exemptLoopback;

    /// <summary>Builds an authenticator from the server's <see cref="AuthenticationOptions"/>.</summary>
    /// <param name="options">The server auth configuration (users, legacy password, disable flag).</param>
    /// <param name="exemptLoopback">
    /// Whether a loopback client skips <c>AUTH</c> (the C6.x exemption, true in production). The
    /// embedded test server sets this false so every integration test exercises <c>AUTH</c> even on
    /// loopback — the SecurityPolicy note's "HighwayTestServer authenticates by default".
    /// </param>
    public PasswordAuthenticator(AuthenticationOptions options, bool exemptLoopback = true)
    {
        _authDisabled = options.ExplicitlyDisabled;
        _exemptLoopback = exemptLoopback;
        _userHashes = options.Users.ToDictionary(u => u.Name, u => u.PasswordHash, StringComparer.Ordinal);
        _legacyDefaultPassword = string.IsNullOrEmpty(options.Password)
            ? null
            : Encoding.UTF8.GetBytes(options.Password);
    }

    /// <summary>Test/compat constructor: a single plaintext password (or none) and the disable flag.</summary>
    public PasswordAuthenticator(string? password, bool authDisabled, bool exemptLoopback = true)
    {
        _authDisabled = authDisabled;
        _exemptLoopback = exemptLoopback;
        _userHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        _legacyDefaultPassword = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
    }

    private bool AuthConfigured => _userHashes.Count > 0 || _legacyDefaultPassword is not null;

    public bool IsPreAuthorized(EndPoint? remote)
    {
        if (_authDisabled || !AuthConfigured)
            return true;

        // Loopback exemption (SecurityPolicy): a client on the loopback interface is trusted —
        // unless the host opted out (the test server does, to exercise AUTH on loopback).
        return _exemptLoopback && remote is IPEndPoint { Address: var addr } && IPAddress.IsLoopback(addr);
    }

    public bool TryAuthenticate(string? username, string password)
    {
        // No credentials configured — any AUTH succeeds (matches "authentication not required").
        if (!AuthConfigured)
            return true;

        var user = username ?? "default";

        // Config users (PBKDF2) take precedence.
        if (_userHashes.TryGetValue(user, out var hash))
            return PasswordHash.Verify(password, hash);

        // Legacy single password: only the `default` user, constant-time compare.
        if (user == "default" && _legacyDefaultPassword is not null)
        {
            var supplied = Encoding.UTF8.GetBytes(password);
            return CryptographicOperations.FixedTimeEquals(supplied, _legacyDefaultPassword);
        }

        return false;
    }
}
