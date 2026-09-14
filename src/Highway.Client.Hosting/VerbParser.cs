namespace Highway.Client.Hosting;

/// <summary>
/// Parses the first argument as a service verb (<c>install</c>, <c>uninstall</c>,
/// <c>start</c>, <c>stop</c>, <c>status</c>) and extracts verb options. Everything
/// after <c>--</c> is captured as passthrough arguments stored in the service's
/// start command.
/// </summary>
/// <remarks>
/// The parser never throws. Invalid input produces a <see cref="VerbParseResult"/>
/// with <see cref="VerbParseResult.Error"/> set and
/// <see cref="VerbParseResult.ExitCode"/> = <see cref="ExitCodes.InvalidArguments"/>.
/// </remarks>
internal static class VerbParser
{
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "install", "uninstall", "start", "stop", "status"
    };

    /// <summary>
    /// Attempts to parse a verb from <paramref name="args"/>. Returns null when the
    /// first argument is not a recognized verb (the app should run normally).
    /// Returns a <see cref="VerbParseResult"/> with <see cref="VerbParseResult.Error"/>
    /// set when a verb is recognized but options are invalid.
    /// </summary>
    public static VerbParseResult? Parse(string[] args)
    {
        if (args.Length == 0)
            return null;

        var first = args[0];
        if (!Verbs.Contains(first))
            return null;

        var verb = first.ToLowerInvariant();
        var options = new VerbOptions();
        var passthrough = new List<string>();
        var inPassthrough = false;

        for (var i = 1; i < args.Length; i++)
        {
            if (inPassthrough)
            {
                passthrough.Add(args[i]);
                continue;
            }

            if (args[i] == "--")
            {
                if (verb != "install")
                    return Error($"Passthrough arguments (--) are only valid with the 'install' verb.");

                inPassthrough = true;
                continue;
            }

            switch (args[i])
            {
                case "--name":
                    if (!TryRequireValue(args, ref i, "--name", out var name, out var nameErr))
                        return nameErr;
                    options.Name = name;
                    break;

                case "--display-name":
                    if (!TryRequireValue(args, ref i, "--display-name", out var display, out var displayErr))
                        return displayErr;
                    options.DisplayName = display;
                    break;

                case "--description":
                    if (!TryRequireValue(args, ref i, "--description", out var desc, out var descErr))
                        return descErr;
                    options.Description = desc;
                    break;

                case "--user":
                    if (verb != "install")
                        return Error($"--user is only valid with the 'install' verb.");
                    if (!TryRequireValue(args, ref i, "--user", out var user, out var userErr))
                        return userErr;
                    options.User = user;
                    break;

                case "--start":
                    if (verb != "install")
                        return Error($"--start is only valid with the 'install' verb.");
                    options.StartAfterInstall = true;
                    break;

                default:
                    return Error($"Unknown option '{args[i]}'.");
            }
        }

        return new VerbParseResult
        {
            Verb = verb,
            Options = options,
            PassthroughArgs = passthrough.Count > 0 ? [.. passthrough] : [],
        };
    }

    private static bool TryRequireValue(
        string[] args, ref int index, string flag,
        out string value, out VerbParseResult error)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--"))
        {
            value = null!;
            error = Error($"{flag} requires a value.");
            return false;
        }

        value = args[++index];
        error = null!;
        return true;
    }

    private static VerbParseResult Error(string message) => new()
    {
        Error = message,
        ExitCode = ExitCodes.InvalidArguments
    };

    /// <summary>The usage text shown when an option error occurs.</summary>
    public static string Usage() => """
        Usage:
          <app> install   [--name X] [--display-name Y] [--description Z] [--user U] [--start] [-- args...]
          <app> uninstall [--name X]
          <app> start     [--name X]
          <app> stop      [--name X]
          <app> status    [--name X]
        """;
}

/// <summary>The result of verb parsing: either a valid verb + options, or an error.</summary>
internal sealed class VerbParseResult
{
    /// <summary>The normalized verb name (lowercase): install, uninstall, start, stop, status.</summary>
    public string Verb { get; init; } = null!;

    /// <summary>Parsed verb options.</summary>
    public VerbOptions Options { get; init; } = new();

    /// <summary>Arguments after <c>--</c>, stored in the service start command.</summary>
    public string[] PassthroughArgs { get; init; } = [];

    /// <summary>Error message when parsing failed (verb was recognized but options are invalid).</summary>
    public string? Error { get; init; }

    /// <summary>Exit code — <see cref="ExitCodes.InvalidArguments"/> on error, 0 otherwise.</summary>
    public int ExitCode { get; init; }

    /// <summary>True when parsing failed.</summary>
    public bool IsError => Error is not null;
}

/// <summary>
/// The option values extracted from the verb's arguments.
/// Every field is null/false when not specified on the command line.
/// </summary>
public sealed class VerbOptions
{
    /// <summary>--name: override the service name.</summary>
    public string? Name { get; internal set; }

    /// <summary>--display-name: override the display name.</summary>
    public string? DisplayName { get; internal set; }

    /// <summary>--description: override the description.</summary>
    public string? Description { get; internal set; }

    /// <summary>--user: Linux user to run as (install only).</summary>
    public string? User { get; internal set; }

    /// <summary>--start: start the service immediately after install.</summary>
    public bool StartAfterInstall { get; internal set; }
}
