using System.Net;

namespace Highway.Samples;

/// <summary>
/// Resolves a setting from a command-line argument, then an environment
/// variable, then a default.
///
/// <para>Deliberately tiny. The samples exist to demonstrate Highway, not
/// configuration binding — pulling in the configuration stack would add noise
/// that has nothing to do with what a reader came here to learn. Linked into
/// each sample app rather than given its own project for the same reason.</para>
/// </summary>
internal static class SampleConfig
{
    /// <summary>Reads <c>--name value</c>, else the environment variable, else the default.</summary>
    public static string String(string[] args, string argName, string envName, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], argName, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        var env = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(env) ? fallback : env;
    }

    /// <summary>Integer form of <see cref="String"/>; falls back when unparseable.</summary>
    public static int Int(string[] args, string argName, string envName, int fallback)
        => int.TryParse(String(args, argName, envName, fallback.ToString()), out var value)
            ? value
            : fallback;

    /// <summary>Parses a bind address, failing loudly rather than silently binding somewhere unintended.</summary>
    public static IPAddress Address(string[] args, string argName, string envName, IPAddress fallback)
    {
        var raw = String(args, argName, envName, fallback.ToString());
        if (IPAddress.TryParse(raw, out var address))
            return address;

        throw new ArgumentException($"'{raw}' is not a valid IP address (from {argName} / {envName}).");
    }
}
