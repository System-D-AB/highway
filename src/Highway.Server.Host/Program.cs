using System.Reflection;
using System.Runtime.InteropServices;
using Highway.Server.Host.Configuration;
using Microsoft.Extensions.Hosting;

namespace Highway.Server.Host;

/// <summary>
/// <c>highways</c> — the Highway broker and dashboard as a standalone executable
/// (feature 031). The executable is a consumer of the same public
/// <see cref="HighwayServerBuilder"/> path the samples use: it adds configuration
/// loading, service-lifetime integration and installer verbs, and no broker behavior.
/// </summary>
public static class Program
{
    /// <summary>Entry point. Returns a process exit code from <see cref="ExitCodes"/>.</summary>
    public static int Main(string[] args) => Run(args);

    /// <summary>
    /// Dispatches the command line. Verbs are handled before any host exists
    /// (design § Host Lifecycle); with no verb the process runs the broker.
    /// <paramref name="environment"/> is injectable for tests; null means the real
    /// process environment.
    /// </summary>
    internal static int Run(string[] args, System.Collections.IDictionary? environment = null)
    {
        HostArguments parsed;
        try
        {
            parsed = HostArguments.Parse(args);
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Known arguments:");
            Console.Error.WriteLine(Usage());
            return ExitCodes.Unexpected;
        }

        if (parsed.ShowVersion)
        {
            PrintVersion(Console.Out);
            return ExitCodes.Success;
        }

        if (parsed.Verb is not null)
        {
            Console.Error.WriteLine(
                $"{parsed.Verb} is not available in this build — service verbs land with feature 031 Phase 3.");
            return ExitCodes.Unexpected;
        }

        if (parsed.Validate)
        {
            try
            {
                var loaded = ConfigurationLoader.Load(
                    parsed.ConfigPath,
                    environment,
                    cliPort: parsed.Port,
                    cliBindAddress: parsed.BindAddress,
                    cliDataDir: parsed.DataDir);

                Console.WriteLine(loaded.SourcePath is null
                    ? "No configuration file found — showing code defaults with environment and command-line overrides."
                    : $"Configuration file: {loaded.SourcePath}");
                EffectiveConfigurationPrinter.Print(loaded.Configuration, Console.Out);
                return ExitCodes.Success;
            }
            catch (ConfigurationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.ConfigurationInvalid;
            }
        }

        // Run mode: configuration → host → run until Ctrl+C / SIGTERM / service stop.
        LoadedConfiguration loadedConfig;
        try
        {
            var configPath = parsed.ConfigPath ?? DiscoverConfigFile();

            if (configPath is null)
                Console.Error.WriteLine(
                    "warning: no highway.json found (looked in the working directory, its conf/ subdirectory " +
                    "and beside the executable) — running with code defaults: loopback, durable beside the " +
                    "executable, no dashboard.");

            loadedConfig = ConfigurationLoader.Load(
                configPath,
                environment,
                cliPort: parsed.Port,
                cliBindAddress: parsed.BindAddress,
                cliDataDir: parsed.DataDir);
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.ConfigurationInvalid;
        }

        try
        {
            var app = HostFactory.Create(loadedConfig.Configuration).Build();
            app.Run();   // blocks; Ctrl+C / SIGTERM / service stop drive the graceful shutdown
            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return IsDataDirectoryFailure(ex) ? ExitCodes.DataDirectoryUnusable : ExitCodes.Unexpected;
        }
    }

    /// <summary>
    /// Configuration discovery (design § Host Lifecycle): the working directory first
    /// (where the operator is standing), then its <c>conf/</c> subdirectory, then beside
    /// the executable (where a bare <c>bin/highways</c> invocation runs). An explicit
    /// <c>--config</c> always wins and never reaches this method.
    /// </summary>
    internal static string? DiscoverConfigFile()
    {
        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), "highway.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "conf", "highway.json"),
            Path.Combine(AppContext.BaseDirectory, "highway.json"),
            Path.Combine(AppContext.BaseDirectory, "conf", "highway.json"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The builder's data-directory failures (unwritable directory, storage-format
    /// mismatch) are the operator-fixable class; everything else is unexpected. Both
    /// carry the cause and the ways out in their own messages — the host maps codes,
    /// it does not paraphrase (design § Error Handling).
    /// </summary>
    private static bool IsDataDirectoryFailure(Exception ex)
        => ex.Message.Contains("data directory", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("storage format", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Prints the product version, the durable storage format and the runtime
    /// identifier (R1.3): an operator upgrading in place can ask both binaries
    /// what they are before touching data.
    /// </summary>
    private static void PrintVersion(TextWriter writer)
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";

        writer.WriteLine($"highways {version}");
        writer.WriteLine($"  storage format : {HighwayServerBuilder.StorageFormatVersion}");
        writer.WriteLine($"  runtime        : {RuntimeInformation.RuntimeIdentifier}");
    }

    private static string Usage() => """
          --version                 print version, storage format and RID, then exit
          --validate                load and validate configuration, print it masked, exit
          --config <path>           configuration file (default: discovery in CWD, conf/, beside exe)
          --port <n>                override server.port
          --bind <addr>             override server.bindAddress
          --data-dir <path>         override server.dataDir
          --install [--start]       install as a service/daemon (Phase 3)
          --uninstall               stop if running, then remove the service/daemon (Phase 3)
          --status                  report service/daemon state (Phase 3)
          --start | --stop          control an installed service/daemon (Phase 3)
          --service-name <name>     service identity for install verbs (Phase 3)
          --service-display <name>  service display name for install verbs (Phase 3)
        """;
}
