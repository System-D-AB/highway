using System.Runtime.Versioning;
using System.Security.Principal;
using Highway.Client.Hosting;
using SharedWindowsServiceManager = Highway.Client.Hosting.WindowsServiceManager;

namespace Highway.Server.Host;

/// <summary>
/// Adapter over the shared <see cref="Highway.Client.Hosting.WindowsServiceManager"/>
/// (feature 036). Translates the broker's <see cref="HostArguments"/> into the shared
/// <see cref="ServiceIdentity"/> + passthrough model. The server's <c>--config</c>
/// becomes a passthrough argument so its behaviour is unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsServiceManager
{
    private const string DefaultServiceName = "Highway";
    private const string DefaultDisplayName = "Highway Server";
    private const string DefaultDescription = "Highway message broker and dashboard.";

    public static bool IsAdministrator() => SharedWindowsServiceManager.IsAdministrator();

    public static int Dispatch(HostArguments args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows service verbs are only supported on Windows.");
            return ExitCodes.PlatformUnsupported;
        }

        // Map 031's --verb form to 036's bare verb
        var verb = args.Verb switch
        {
            "--install" => "install",
            "--uninstall" => "uninstall",
            "--status" => "status",
            "--start" => "start",
            "--stop" => "stop",
            _ => null
        };

        if (verb is null)
            return ExitCodes.Unexpected;

        // Build identity from broker defaults + verb options
        var verbOptions = new VerbOptions
        {
            Name = args.ServiceName,
            DisplayName = args.ServiceDisplayName,
        };

        var codeOptions = new HostingOptions
        {
            ServiceName = DefaultServiceName,
            DisplayName = DefaultDisplayName,
            Description = DefaultDescription,
        };

        var identityResult = ServiceIdentity.Resolve(verbOptions, codeOptions);
        if (!identityResult.IsOk)
        {
            Console.Error.WriteLine(identityResult.Error);
            return ExitCodes.Unexpected;
        }

        // The server's --config becomes a passthrough arg
        var passthrough = new List<string>();
        if (args.ConfigPath is not null)
        {
            passthrough.Add("--config");
            passthrough.Add(args.ConfigPath);
        }

        return SharedWindowsServiceManager.Dispatch(
            verb,
            identityResult.Identity!,
            [.. passthrough],
            args.StartAfterInstall);
    }
}
