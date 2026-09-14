using Highway.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Highway.Client.Hosting;

/// <summary>
/// One-line host for any .NET app. Runs as a console app, a Windows service, or
/// a systemd unit — with optional Highway integration.
///
/// <code>
/// // Plain app (no Highway):
/// return await HighwayHost.RunAsync(args);
///
/// // Highway app:
/// return await HighwayHost.RunAsync(args, o => o.Server = "localhost:6379");
///
/// // Full control:
/// return await HighwayHost.RunAsync(args, o => o.Server = "...",
///     b => b.Services.AddHostedService&lt;MyWorker&gt;());
/// </code>
/// </summary>
public static class HighwayHost
{
    /// <summary>
    /// Runs the app with no Highway integration and no extra service registration.
    /// Service verbs (<c>install</c>, <c>uninstall</c>, …) are dispatched before
    /// any host is built.
    /// </summary>
    /// <param name="args">Command-line arguments (first arg may be a verb).</param>
    /// <param name="ct">Optional cancellation token for programmatic shutdown.</param>
    /// <returns>A process exit code from <see cref="ExitCodes"/>.</returns>
    public static Task<int> RunAsync(string[] args, CancellationToken ct = default)
        => RunAsync(args, highway: null, configure: null, hosting: null, ct: ct);

    /// <summary>
    /// Runs the app with Highway integration. <paramref name="highway"/> configures
    /// the Highway client engine; pass <c>null</c> to skip Highway registration.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="highway">Highway client configuration, or <c>null</c>.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public static Task<int> RunAsync(
        string[] args,
        Action<HighwayOptions>? highway,
        CancellationToken ct = default)
        => RunAsync(args, highway, configure: null, hosting: null, ct: ct);

    /// <summary>
    /// Runs the app with Highway integration and custom host builder configuration.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="highway">Highway client configuration, or <c>null</c>.</param>
    /// <param name="configure">Custom host builder configuration (add hosted services, logging, etc.).</param>
    /// <param name="ct">Optional cancellation token.</param>
    public static Task<int> RunAsync(
        string[] args,
        Action<HighwayOptions>? highway,
        Action<HostApplicationBuilder> configure,
        CancellationToken ct = default)
        => RunAsync(args, highway, configure, hosting: null, ct: ct);

    /// <summary>
    /// Runs the app with Highway integration, custom host builder configuration,
    /// and hosting options (service name, description, shutdown timeout).
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="highway">Highway client configuration, or <c>null</c>.</param>
    /// <param name="configure">Custom host builder configuration, or <c>null</c>.</param>
    /// <param name="hosting">Hosting/service identity options, or <c>null</c>.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public static async Task<int> RunAsync(
        string[] args,
        Action<HighwayOptions>? highway,
        Action<HostApplicationBuilder>? configure,
        HostingOptions? hosting,
        CancellationToken ct = default)
    {
        // ── Verb dispatch (before any host is built) ──────────────────────

        var verbResult = VerbParser.Parse(args);
        if (verbResult is not null)
        {
            if (verbResult.IsError)
            {
                Console.Error.WriteLine(verbResult.Error);
                Console.Error.WriteLine(VerbParser.Usage());
                return verbResult.ExitCode;
            }

            return DispatchVerb(verbResult, hosting);
        }

        // ── Host mode ─────────────────────────────────────────────────────

        try
        {
            // Content root must be set at builder creation — mutating
            // Configuration[ContentRootKey] afterwards does not move the already-created
            // environment. BaseDirectory makes relative paths (appsettings.json) work
            // the same in all environments: console cwd, SCM's system32, systemd's /.
            // Args flow into configuration (standard --Key=Value command-line config);
            // on this path they contain no verb — verbs returned above.
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings
                {
                    Args = args,
                    ContentRootPath = AppContext.BaseDirectory,
                });

            // Shutdown timeout from hosting options
            var shutdownTimeout = hosting?.ShutdownTimeout ?? TimeSpan.FromSeconds(30);
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = shutdownTimeout);

            // Both registered; each no-ops off its platform.
            builder.Services.AddWindowsService();
            builder.Services.AddSystemd();

            // Highway — only when configured
            if (highway is not null)
            {
                builder.Services.AddHighway(highway);
            }

            // App's own registrations
            configure?.Invoke(builder);

            using var host = builder.Build();
            await host.RunAsync(ct);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Unexpected;
        }
    }

    private static int DispatchVerb(VerbParseResult parsed, HostingOptions? hosting)
    {
        // Resolve identity: verb options → code options → convention
        var identityResult = ServiceIdentity.Resolve(parsed.Options, hosting);
        if (!identityResult.IsOk)
        {
            Console.Error.WriteLine(identityResult.Error);
            return ExitCodes.InvalidArguments;
        }

        var identity = identityResult.Identity!;

        if (OperatingSystem.IsWindows())
        {
            return WindowsServiceManager.Dispatch(
                parsed.Verb,
                identity,
                parsed.PassthroughArgs,
                parsed.Options.StartAfterInstall);
        }

        if (OperatingSystem.IsLinux())
        {
            var mgr = new SystemdServiceManager();
            return mgr.Dispatch(
                parsed.Verb,
                identity,
                parsed.PassthroughArgs,
                parsed.Options.StartAfterInstall);
        }

        Console.Error.WriteLine("Service verbs are only supported on Windows and Linux.");
        return ExitCodes.PlatformUnsupported;
    }
}
