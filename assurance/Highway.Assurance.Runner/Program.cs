namespace Highway.Assurance.Runner;

using Highway.Assurance.Runner.Models;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var profileType = GetArg(args, "--profile", "standard").ToLowerInvariant();
        var profile = profileType switch
        {
            "shortened" or "ci" => RunProfile.CreateShortened(),
            _ => RunProfile.CreateDefault()
        };

        if (int.TryParse(GetArg(args, "--rate", ""), out var customRate))
            profile.TargetRatePerSec = customRate;

        if (int.TryParse(GetArg(args, "--lease", ""), out var customLease))
            profile.LeaseSeconds = customLease;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
        var defaultRunDir = Path.Combine(Directory.GetCurrentDirectory(), "assurance", "runs", timestamp);
        var runDir = Path.GetFullPath(GetArg(args, "--run-dir", defaultRunDir));

        // --doorbells off runs the second gate-G3 pass: DoorbellsEnabled == false on every
        // workload, leaving only the backstop sweep to drive correctness (041 R3.2). Default on.
        var doorbells = GetArg(args, "--doorbells", "on").ToLowerInvariant();
        var doorbellsEnabled = doorbells is not ("off" or "false" or "0" or "no");

        // --herd runs the failing-over two-node variant (042-1d D-T6): the primary broker
        // is killed mid-turbulence and the herd converges on the standby.
        var herd = GetArg(args, "--herd", "off").ToLowerInvariant() is "on" or "true" or "1" or "yes";

        var orchestrator = new Orchestrator();
        var result = herd
            ? await orchestrator.ExecuteHerdRunAsync(runDir, profile, doorbellsEnabled)
            : await orchestrator.ExecuteRunAsync(runDir, profile, doorbellsEnabled);

        return result.ExitCode;
    }

    private static string GetArg(string[] args, string name, string defaultValue)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return defaultValue;
    }
}
