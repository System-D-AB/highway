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
            if (OperatingSystem.IsWindows())
            {
                return WindowsServiceManager.Dispatch(parsed);
            }

            Console.Error.WriteLine("Windows service verbs are only supported on Windows.");
            return ExitCodes.PlatformUnsupported;
        }

        if (parsed.Promote)
            return DispatchReplVerb(parsed, environment, "HW.REPL.PROMOTE", parsed.PromoteReason, "promoted epoch={0}");

        if (parsed.Goodbye)
            return DispatchReplVerb(parsed, environment, "HW.REPL.GOODBYE", parsed.GoodbyeReason, "goodbye begun ({0}); the node drains, then stands down");

        if (parsed.DrainAndStop)
            return DispatchDrainAndStop(parsed, environment);

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
                    "warning: no highway.json found (looked in the working directory, its config/ subdirectory " +
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
    /// (where the operator is standing), then its <c>config/</c> subdirectory, then beside
    /// the executable (where a bare <c>bin/highways</c> invocation runs). An explicit
    /// <c>--config</c> always wins and never reaches this method.
    /// </summary>
    internal static string? DiscoverConfigFile()
    {
        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), "highway.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "config", "highway.json"),
            Path.Combine(AppContext.BaseDirectory, "highway.json"),
            Path.Combine(AppContext.BaseDirectory, "config", "highway.json"),
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

    /// <summary>042 T5 / 042-1c C-T5 — an operator replication verb (promote, goodbye) against the running broker.</summary>
    private static int DispatchReplVerb(
        HostArguments parsed, System.Collections.IDictionary? environment,
        string command, string? reason, string successFormat)
    {
        try
        {
            var loaded = ConfigurationLoader.Load(
                parsed.ConfigPath ?? DiscoverConfigFile(),
                environment,
                cliPort: parsed.Port,
                cliBindAddress: parsed.BindAddress,
                cliDataDir: parsed.DataDir);

            var c = loaded.Configuration;
            var endpoint = $"{c.Server.BindAddress}:{c.Server.Port}";
            if (!string.IsNullOrEmpty(c.Authentication.Password))
                endpoint += $",password={c.Authentication.Password}";

            using var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(endpoint);
            var result = mux.GetDatabase().Execute(command, reason ?? "host-verb");
            Console.WriteLine(string.Format(successFormat, result));
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Unexpected;
        }
    }

    /// <summary>
    /// 050 T7 — the safe-upgrade one-liner: hand the master off with <c>HW.REPL.GOODBYE</c>, wait for
    /// the drain to complete, then stop the service cleanly. The operator's whole rolling-upgrade step
    /// for the primary becomes <c>--drain-and-stop → swap binaries → --start</c>, with the failover
    /// made deliberate and lossless instead of an ungraceful restart that degrades the cluster.
    /// </summary>
    private static int DispatchDrainAndStop(HostArguments parsed, System.Collections.IDictionary? environment)
    {
        try
        {
            var loaded = ConfigurationLoader.Load(
                parsed.ConfigPath ?? DiscoverConfigFile(),
                environment,
                cliPort: parsed.Port,
                cliBindAddress: parsed.BindAddress,
                cliDataDir: parsed.DataDir);

            var c = loaded.Configuration;
            var endpoint = $"{c.Server.BindAddress}:{c.Server.Port}";
            if (!string.IsNullOrEmpty(c.Authentication.Password))
                endpoint += $",password={c.Authentication.Password}";

            using var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(endpoint);
            var db = mux.GetDatabase();

            Console.WriteLine("Draining: HW.REPL.GOODBYE (graceful hand-off to the successor)…");
            try { db.Execute("HW.REPL.GOODBYE", "drain-and-stop"); }
            catch (Exception ex) { Console.WriteLine($"  goodbye not applicable ({ex.Message}); proceeding to stop"); }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            var drained = false;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                var fields = ReadReplStatus(db);
                if (fields is null) { drained = true; break; }   // no replication → nothing to drain
                fields.TryGetValue("repl.draining", out var draining);
                fields.TryGetValue("repl.role", out var role);
                fields.TryGetValue("repl.clients", out var clients);
                if (!string.Equals(draining, "True", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(role, "Demoted", StringComparison.OrdinalIgnoreCase) || clients == "0"))
                {
                    drained = true;
                    break;
                }
            }

            Console.WriteLine(drained
                ? "Drained; the node has stood down. Stopping the service…"
                : "Drain timed out; stopping anyway (any in-flight work replays to the new master).");

            if (OperatingSystem.IsWindows())
                return WindowsServiceManager.Dispatch(HostArguments.Parse(["--stop"]));

            Console.WriteLine("Node drained. Stop the unit to finish: 'systemctl stop <your-highway-unit>', then swap binaries and start.");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Unexpected;
        }
    }

    private static Dictionary<string, string>? ReadReplStatus(StackExchange.Redis.IDatabase db)
    {
        try
        {
            var flat = (StackExchange.Redis.RedisResult[])db.Execute("HW.REPL.STATUS")!;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < flat.Length; i += 2)
                fields[flat[i].ToString()!] = flat[i + 1].ToString()!;
            return fields;
        }
        catch
        {
            return null;   // a broker without replication configured
        }
    }

    private static string Usage() => """
          --promote [reason]         issue HW.REPL.PROMOTE against the configured broker, then exit
          --goodbye [reason]         issue HW.REPL.GOODBYE (graceful drain + stand-down), then exit
          --drain-and-stop           GOODBYE, wait for the drain, then stop the service (safe rolling upgrade)
          --version                 print version, storage format and RID, then exit
          --validate                load and validate configuration, print it masked, exit
          --config <path>           configuration file (default: discovery in CWD, config/, beside exe)
          --port <n>                override server.port
          --bind <addr>             override server.bindAddress
          --data-dir <path>         override server.dataDir
          --install [--start]       install as a service/daemon
          --uninstall               stop if running, then remove the service/daemon
          --status                  report service/daemon state
          --start | --stop          control an installed service/daemon
          --service-name <name>     service identity for install verbs
          --service-display <name>  service display name for install verbs
        """;
}
