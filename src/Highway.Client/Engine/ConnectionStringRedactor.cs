using System.Text;

namespace Highway.Client.Engine;

/// <summary>
/// Removes credentials from a connection string so it can be logged or put in an
/// exception message (feature 012).
///
/// <para><b>Every place a connection string leaves the process goes through here.</b>
/// Three sites already leaked before this existed: the engine logged the raw string at
/// <c>Information</c> level, and two exception paths embedded it. None of them were
/// dangerous while Highway had no authentication — all three became credential leaks the
/// moment it did.</para>
///
/// <para>The raw string is redacted textually rather than by round-tripping through
/// <c>ConfigurationOptions</c>, because the path most likely to hold a secret is the one
/// where parsing <i>failed</i> and there is no parsed instance to ask.</para>
/// </summary>
internal static class ConnectionStringRedactor
{
    private const string Replacement = "***";

    /// <summary>Option names whose values are secret.</summary>
    private static readonly string[] SecretKeys = ["password", "pwd", "user", "username"];

    /// <summary>
    /// Returns <paramref name="configuration"/> with any credential values replaced.
    /// Never throws: a redactor that can fail on malformed input is a redactor that leaks
    /// on malformed input, which is exactly when it matters most.
    /// </summary>
    public static string Redact(string? configuration)
    {
        if (string.IsNullOrEmpty(configuration))
            return configuration ?? string.Empty;

        try
        {
            var result = new StringBuilder(configuration.Length);

            foreach (var part in configuration.Split(','))
            {
                if (result.Length > 0) result.Append(',');

                var eq = part.IndexOf('=');
                if (eq < 0)
                {
                    result.Append(part);
                    continue;
                }

                var key = part.AsSpan(0, eq).Trim();
                var isSecret = false;
                foreach (var secret in SecretKeys)
                {
                    if (key.Equals(secret, StringComparison.OrdinalIgnoreCase))
                    {
                        isSecret = true;
                        break;
                    }
                }

                result.Append(part.AsSpan(0, eq + 1));
                result.Append(isSecret ? Replacement : part.AsSpan(eq + 1));
            }

            return result.ToString();
        }
        catch
        {
            // Unparseable in some way we did not anticipate. Redact the whole thing rather
            // than risk emitting a secret we failed to find.
            return Replacement;
        }
    }
}
