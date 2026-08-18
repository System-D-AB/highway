using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Highway.Server.Host.Configuration;

/// <summary>
/// The result of loading: the merged configuration and the file it came from, when any.
/// </summary>
internal sealed class LoadedConfiguration
{
    public required HostConfiguration Configuration { get; init; }

    /// <summary>The configuration file these values came from; null when only defaults, environment or CLI apply.</summary>
    public string? SourcePath { get; init; }
}

/// <summary>
/// Loads <c>highways</c>'s configuration in precedence order — <b>defaults &lt; file &lt;
/// environment &lt; command line</b> (feature 031 R2.3) — then resolves relative paths
/// (design § D4) and validates, naming the offending key on any failure.
/// </summary>
internal static class ConfigurationLoader
{
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Loads and validates the configuration. <paramref name="configPath"/> is an explicit
    /// file (discovery of the default locations is the host's job, not the loader's);
    /// the <c>cli*</c> values are the command-line overrides, which beat everything.
    /// </summary>
    public static LoadedConfiguration Load(
        string? configPath,
        IDictionary? environment = null,
        int? cliPort = null,
        string? cliBindAddress = null,
        string? cliDataDir = null)
    {
        var configuration = new HostConfiguration();   // the defaults live in the DTO
        var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? source = null;

        if (configPath is not null)
        {
            source = Path.GetFullPath(configPath);

            if (!File.Exists(source))
                throw new ConfigurationException($"configuration file not found: {source}");

            configuration = ReadFile(source);
        }

        EnvironmentOverrides.Apply(configuration, environment ?? Environment.GetEnvironmentVariables(), overridden);

        if (cliPort is not null)
        {
            configuration.Server.Port = cliPort.Value;
            overridden.Add("server.port");
        }

        if (cliBindAddress is not null)
        {
            configuration.Server.BindAddress = cliBindAddress;
            overridden.Add("server.bindAddress");
        }

        if (cliDataDir is not null)
        {
            configuration.Server.DataDir = cliDataDir;
            overridden.Add("server.dataDir");
        }

        ResolveRelativePaths(configuration, source is null ? null : Path.GetDirectoryName(source), overridden);

        configuration.Validate();

        return new LoadedConfiguration { Configuration = configuration, SourcePath = source };
    }

    private static HostConfiguration ReadFile(string source)
    {
        try
        {
            return JsonSerializer.Deserialize<HostConfiguration>(File.ReadAllText(source), JsonOptions)
                   ?? throw new ConfigurationException($"{source} is empty.");
        }
        catch (JsonException ex)
        {
            var location = string.IsNullOrEmpty(ex.Path) ? "" : $" at '{ex.Path}'";
            throw new ConfigurationException($"{source}{location}: {ex.Message}");
        }
    }

    /// <summary>
    /// Relative paths from the file resolve against the file's own directory; relative
    /// paths that arrived via the environment or the command line resolve against the
    /// current directory (their basis is wherever the operator is standing); absolute
    /// paths are honored verbatim (design § D4).
    /// </summary>
    private static void ResolveRelativePaths(HostConfiguration c, string? configDir, HashSet<string> overridden)
    {
        c.Server.DataDir = Resolve(c.Server.DataDir, "server.dataDir");
        c.Authentication.AclFile = Resolve(c.Authentication.AclFile, "authentication.aclFile");
        c.Tls.CertFile = Resolve(c.Tls.CertFile, "tls.certFile");
        c.Tls.IssuerCertificatePath = Resolve(c.Tls.IssuerCertificatePath, "tls.issuerCertificatePath");

        string? Resolve(string? value, string path)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            if (Path.IsPathRooted(value))
                return Path.GetFullPath(value);

            var basis = !overridden.Contains(path) && configDir is not null
                ? configDir
                : Directory.GetCurrentDirectory();

            return Path.GetFullPath(Path.Combine(basis, value));
        }
    }
}
