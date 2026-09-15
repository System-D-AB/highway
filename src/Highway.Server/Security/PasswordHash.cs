using System.Security.Cryptography;

namespace Highway.Server.Security;

/// <summary>
/// PBKDF2 password hashing for Highway config users (040 T6 / 037 R11). A hash is a single
/// self-describing string so a <c>highway.json</c> users list can carry it verbatim and the
/// server needs no side-channel to know how it was produced:
///
/// <code>
///   PBKDF2$&lt;iterations&gt;$&lt;base64(salt)&gt;$&lt;base64(derivedKey)&gt;
/// </code>
///
/// <para><b>The recipe</b> (documented so an operator can reproduce it, and so the format is a
/// contract, not a coincidence): PBKDF2 over HMAC-SHA256, a 16-byte random salt, a 32-byte
/// derived key, and <see cref="DefaultIterations"/> iterations. The iteration count and salt
/// travel <i>in</i> the hash so raising the count later does not strand existing users — each
/// hash verifies against the parameters it was minted with.</para>
///
/// <para>Verification is constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>):
/// a wrong password takes the same time as a right one of equal length, so the compare leaks
/// nothing timeable (037 R11).</para>
/// </summary>
internal static class PasswordHash
{
    /// <summary>The scheme tag; the first <c>$</c>-delimited field of every hash.</summary>
    public const string Scheme = "PBKDF2";

    /// <summary>Default work factor. Raise over time; existing hashes keep verifying at their own count.</summary>
    public const int DefaultIterations = 210_000;

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private static readonly HashAlgorithmName Prf = HashAlgorithmName.SHA256;

    /// <summary>Mints a hash string for <paramref name="password"/> using the documented recipe.</summary>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Prf, KeyBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against a <see cref="Hash"/> string. Returns false for
    /// a wrong password <b>or</b> a malformed/unknown-scheme hash — never throws, so a bad config
    /// entry fails closed rather than crashing the auth path.
    /// </summary>
    public static bool Verify(string password, string hash)
    {
        var parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme)
            return false;

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Prf, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True if <paramref name="value"/> is a well-formed PBKDF2 hash string (vs a plaintext password).</summary>
    public static bool IsHash(string? value)
        => value is not null && value.StartsWith(Scheme + "$", StringComparison.Ordinal);
}
