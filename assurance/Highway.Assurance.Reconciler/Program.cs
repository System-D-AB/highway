namespace Highway.Assurance.Reconciler;

using System.Text.Json;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var runDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        Console.WriteLine($"[Reconciler] Reading run directory: {runDir}");

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        Console.WriteLine($"[Reconciler] Verdict: {result.Verdict} (Exit code {result.ExitCode})");
        foreach (var (name, inv) in result.Invariants)
        {
            var icon = inv.Passed ? "✓" : "✗";
            Console.WriteLine($"  {icon} {name,-26}: {inv.Verdict,-16} | {inv.Notes}");
        }

        if (result.Errors.Count > 0)
        {
            Console.WriteLine("[Reconciler] Errors:");
            foreach (var err in result.Errors)
            {
                Console.WriteLine($"  - {err}");
            }
        }

        Console.WriteLine($"[Reconciler] Reports written to {Path.Combine(runDir, "report.md")} and report.json");
        return result.ExitCode;
    }
}
