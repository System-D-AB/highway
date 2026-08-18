namespace Highway.Server.Host;

/// <summary>
/// A command-line error whose message names the offending argument.
/// </summary>
public sealed class CommandLineException(string message) : Exception(message);

/// <summary>
/// The parsed command line of <c>highways</c> (feature 031, design § Command-line
/// arguments). Verbs are recognized positionally — the first argument decides — and
/// everything is parsed before any host exists.
/// </summary>
internal sealed class HostArguments
{
    private static readonly string[] Verbs = ["--install", "--uninstall", "--status", "--start", "--stop"];

    /// <summary>The service verb, in flag form ("--install"), or null in run/validate mode.</summary>
    public string? Verb { get; private set; }

    public bool ShowVersion { get; private set; }
    public bool Validate { get; private set; }
    public string? ConfigPath { get; private set; }
    public int? Port { get; private set; }
    public string? BindAddress { get; private set; }
    public string? DataDir { get; private set; }

    /// <summary>--install --start: install and start immediately.</summary>
    public bool StartAfterInstall { get; private set; }

    public string? ServiceName { get; private set; }
    public string? ServiceDisplayName { get; private set; }

    public static HostArguments Parse(string[] args)
    {
        var result = new HostArguments();

        if (args.Length == 0)
            return result;

        var index = 0;

        if (Verbs.Contains(args[0]))
        {
            result.Verb = args[0];
            index = 1;
        }

        for (; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--version":
                    result.ShowVersion = true;
                    break;

                case "--validate":
                    RejectIfVerb(result, "--validate");
                    result.Validate = true;
                    break;

                case "--config":
                    result.ConfigPath = Path.GetFullPath(RequireValue(args, ref index, "--config"));
                    break;

                case "--port":
                    RejectIfVerb(result, "--port");
                    var portText = RequireValue(args, ref index, "--port");
                    if (!int.TryParse(portText, out var port))
                        throw new CommandLineException($"--port: '{portText}' is not a number.");
                    result.Port = port;
                    break;

                case "--bind":
                    RejectIfVerb(result, "--bind");
                    result.BindAddress = RequireValue(args, ref index, "--bind");
                    break;

                case "--data-dir":
                    RejectIfVerb(result, "--data-dir");
                    result.DataDir = RequireValue(args, ref index, "--data-dir");
                    break;

                case "--start" when result.Verb == "--install":
                    result.StartAfterInstall = true;
                    break;

                case "--service-name":
                    result.ServiceName = RequireValue(args, ref index, "--service-name");
                    break;

                case "--service-display":
                    result.ServiceDisplayName = RequireValue(args, ref index, "--service-display");
                    break;

                default:
                    throw new CommandLineException($"Unknown argument '{args[index]}'.");
            }
        }

        return result;

        static string RequireValue(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
                throw new CommandLineException($"{flag} requires a value.");
            return args[++i];
        }

        static void RejectIfVerb(HostArguments parsed, string flag)
        {
            if (parsed.Verb is not null)
                throw new CommandLineException($"{flag} cannot be combined with the {parsed.Verb} verb.");
        }
    }
}
