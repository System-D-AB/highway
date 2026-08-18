namespace Highway.Assurance.Reconciler.Models;

public sealed class InvariantResult
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Passed { get; set; } = true;
    public string Verdict { get; set; } = "PASS"; // PASS | FAIL | PASS_WITH_NOTES
    public int SentCount { get; set; }
    public int ProcessedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ExpectedMissCount { get; set; }
    public List<string> OffendingCids { get; set; } = [];
    public Dictionary<string, List<string>> OffendingLedgerLines { get; set; } = [];
    public string Notes { get; set; } = "";
}

public sealed class ReconciliationResult
{
    public string RunId { get; set; } = "";
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    public string Verdict { get; set; } = "PASSED"; // PASSED | FAILED | CONFLICT | HARNESS
    public int ExitCode { get; set; }
    public Dictionary<string, InvariantResult> Invariants { get; set; } = [];
    public Dictionary<string, int> TotalEventsByKind { get; set; } = [];
    public Dictionary<string, int> TotalEventsByType { get; set; } = [];
    public Dictionary<string, double> AchievedRateByVerb { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}
