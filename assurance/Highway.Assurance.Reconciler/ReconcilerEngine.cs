namespace Highway.Assurance.Reconciler;

using System.Text;
using System.Text.Json;
using Highway.Assurance.Ledger;
using Highway.Assurance.Reconciler.Models;

public sealed class ReconcilerEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<ReconciliationResult> ReconcileRunDirectoryAsync(string runDir, CancellationToken ct = default)
    {
        var result = new ReconciliationResult
        {
            RunId = Path.GetFileName(runDir.TrimEnd('/', '\\')),
            Timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
        };

        var ledgersDir = Path.Combine(runDir, "ledgers");
        if (!Directory.Exists(ledgersDir))
        {
            result.Verdict = "HARNESS";
            result.ExitCode = 2;
            result.Errors.Add($"Ledgers directory not found at {ledgersDir}");
            return result;
        }

        var ledgerFiles = Directory.GetFiles(ledgersDir, "*.jsonl");
        if (ledgerFiles.Length == 0)
        {
            result.Verdict = "HARNESS";
            result.ExitCode = 2;
            result.Errors.Add($"No ledger (.jsonl) files found in {ledgersDir}");
            return result;
        }

        var rawLinesByCid = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var allEntries = new List<LedgerEntry>();

        foreach (var file in ledgerFiles)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<LedgerEntry>(line);
                    if (entry == null) continue;

                    allEntries.Add(entry);

                    if (!string.IsNullOrEmpty(entry.Cid))
                    {
                        if (!rawLinesByCid.TryGetValue(entry.Cid, out var lines))
                        {
                            lines = [];
                            rawLinesByCid[entry.Cid] = lines;
                        }
                        lines.Add($"[{Path.GetFileName(file)}] {line}");
                    }

                    // Kind stats
                    var kind = entry.Kind;
                    result.TotalEventsByKind[kind] = result.TotalEventsByKind.GetValueOrDefault(kind) + 1;

                    // Type stats
                    if (!string.IsNullOrEmpty(entry.Type))
                    {
                        result.TotalEventsByType[entry.Type] = result.TotalEventsByType.GetValueOrDefault(entry.Type) + 1;
                    }
                }
                catch (JsonException ex)
                {
                    result.Warnings.Add($"Unparseable line in {file}: {ex.Message}");
                }
            }
        }

        // Evaluate Invariants
        EvaluateI1QueueCompleteness(allEntries, rawLinesByCid, result);
        EvaluateI2NoPhantoms(allEntries, rawLinesByCid, result);
        EvaluateI3RpcNeverSilent(allEntries, rawLinesByCid, result);
        EvaluateI4PubSubPerLiveGroup(allEntries, rawLinesByCid, result);
        EvaluateI5Duplicates(allEntries, result);
        await EvaluateI6DeadLettersAsync(runDir, result, ct).ConfigureAwait(false);
        await EvaluateI7NothingLeftBehindAsync(runDir, result, ct).ConfigureAwait(false);

        // Compute overall verdict
        var anyFailed = result.Invariants.Values.Any(i => !i.Passed);
        if (anyFailed)
        {
            result.Verdict = "FAILED";
            result.ExitCode = 1;
        }
        else
        {
            result.Verdict = "PASSED";
            result.ExitCode = 0;
        }

        // Write reports
        var reportJson = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.json"), reportJson, ct).ConfigureAwait(false);

        var reportMd = GenerateMarkdownReport(result);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), reportMd, ct).ConfigureAwait(false);

        return result;
    }

    private static void EvaluateI1QueueCompleteness(List<LedgerEntry> entries, Dictionary<string, List<string>> rawLinesByCid, ReconciliationResult result)
    {
        var sentEntries = entries.Where(e => e.Kind == "sent" && e.Type == "SendEmail" && !string.IsNullOrEmpty(e.Cid)).ToList();
        var processedEntries = entries.Where(e => e.Kind == "processed" && e.Type == "SendEmail" && !string.IsNullOrEmpty(e.Cid)).ToList();

        var sentCids = sentEntries.Select(e => e.Cid!).ToHashSet(StringComparer.Ordinal);
        var processedCids = processedEntries.Select(e => e.Cid!).ToHashSet(StringComparer.Ordinal);

        var missingCids = sentCids.Except(processedCids).ToList();

        var inv = new InvariantResult
        {
            Name = "I1_QueueCompleteness",
            Description = "Every SendEmail id sent by either producer has at least one processed entry (sent ⊆ processed)",
            SentCount = sentCids.Count,
            ProcessedCount = processedCids.Count,
            Passed = missingCids.Count == 0,
            Verdict = missingCids.Count == 0 ? "PASS" : "FAIL"
        };

        if (missingCids.Count > 0)
        {
            inv.OffendingCids = missingCids.Take(20).ToList();
            inv.Notes = $"{missingCids.Count} sent message(s) were never processed.";
            foreach (var cid in inv.OffendingCids)
            {
                if (rawLinesByCid.TryGetValue(cid, out var lines))
                {
                    inv.OffendingLedgerLines[cid] = lines;
                }
            }
        }
        else
        {
            inv.Notes = $"All {sentCids.Count} sent messages were successfully processed.";
        }

        result.Invariants[inv.Name] = inv;
    }

    private static void EvaluateI2NoPhantoms(List<LedgerEntry> entries, Dictionary<string, List<string>> rawLinesByCid, ReconciliationResult result)
    {
        var sentCids = entries.Where(e => e.Kind == "sent" && !string.IsNullOrEmpty(e.Cid)).Select(e => e.Cid!).ToHashSet(StringComparer.Ordinal);
        var processedEntries = entries.Where(e => e.Kind == "processed" && !string.IsNullOrEmpty(e.Cid)).ToList();
        var processedCids = processedEntries.Select(e => e.Cid!).ToHashSet(StringComparer.Ordinal);

        var phantomCids = processedCids.Except(sentCids).ToList();

        var inv = new InvariantResult
        {
            Name = "I2_NoPhantoms",
            Description = "Every processed id was sent by someone (processed ⊆ sent)",
            SentCount = sentCids.Count,
            ProcessedCount = processedCids.Count,
            Passed = phantomCids.Count == 0,
            Verdict = phantomCids.Count == 0 ? "PASS" : "FAIL"
        };

        if (phantomCids.Count > 0)
        {
            inv.OffendingCids = phantomCids.Take(20).ToList();
            inv.Notes = $"{phantomCids.Count} processed message(s) have no corresponding sent record.";
            foreach (var cid in inv.OffendingCids)
            {
                if (rawLinesByCid.TryGetValue(cid, out var lines))
                {
                    inv.OffendingLedgerLines[cid] = lines;
                }
            }
        }
        else
        {
            inv.Notes = "Zero phantom messages found.";
        }

        result.Invariants[inv.Name] = inv;
    }

    private static void EvaluateI3RpcNeverSilent(List<LedgerEntry> entries, Dictionary<string, List<string>> rawLinesByCid, ReconciliationResult result)
    {
        var executedCids = entries.Where(e => e.Kind == "executed" && !string.IsNullOrEmpty(e.Cid)).Select(e => e.Cid!).ToHashSet(StringComparer.Ordinal);
        var answeredCids = entries.Where(e => (e.Kind == "replied" || e.Kind == "timed-out" || e.Kind == "failed") && !string.IsNullOrEmpty(e.Cid)).Select(e => e.Cid!).ToHashSet(StringComparer.Ordinal);

        var silentCids = executedCids.Except(answeredCids).ToList();

        var inv = new InvariantResult
        {
            Name = "I3_RpcNeverSilent",
            Description = "Every executed RPC call has a matching replied or timed-out entry (executed ⊆ replied ∪ timed-out)",
            SentCount = executedCids.Count,
            ProcessedCount = answeredCids.Count,
            Passed = silentCids.Count == 0,
            Verdict = silentCids.Count == 0 ? "PASS" : "FAIL"
        };

        if (silentCids.Count > 0)
        {
            inv.OffendingCids = silentCids.Take(20).ToList();
            inv.Notes = $"{silentCids.Count} executed RPC calls received neither reply nor recorded timeout.";
            foreach (var cid in inv.OffendingCids)
            {
                if (rawLinesByCid.TryGetValue(cid, out var lines))
                {
                    inv.OffendingLedgerLines[cid] = lines;
                }
            }
        }
        else
        {
            inv.Notes = $"All {executedCids.Count} executed RPC calls received replies or timeouts.";
        }

        result.Invariants[inv.Name] = inv;
    }

    private static void EvaluateI4PubSubPerLiveGroup(List<LedgerEntry> entries, Dictionary<string, List<string>> rawLinesByCid, ReconciliationResult result)
    {
        // Expected channel subscriptions:
        // UserSignedUp -> group "notifications-subs"
        // PasswordResetRequested -> group "accounts"
        // AccountAudited -> group "notifications-subs"
        // EmailDispatched -> group "edge"

        var publishedEntries = entries.Where(e => e.Kind == "published" && !string.IsNullOrEmpty(e.Cid)).ToList();
        var receivedEntries = entries.Where(e => e.Kind == "received" && !string.IsNullOrEmpty(e.Cid)).ToList();
        var expectedMissEntries = entries.Where(e => e.Kind == "expected-miss" && !string.IsNullOrEmpty(e.Cid)).ToList();
        var expectedMissCids = expectedMissEntries.Select(e => e.Cid!).ToHashSet(StringComparer.Ordinal);

        var channelExpectedGroups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserSignedUp"] = ["notifications-subs"],
            ["PasswordResetRequested"] = ["accounts"],
            ["AccountAudited"] = ["notifications-subs"],
            ["EmailDispatched"] = ["edge"]
        };

        var receivedMap = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in receivedEntries)
        {
            var grp = !string.IsNullOrEmpty(r.Group) ? r.Group : r.Node;
            receivedMap.Add($"{r.Cid}:{grp}");
        }

        var missingPublishes = new List<(string Cid, string Type, string MissingGroup)>();

        foreach (var pub in publishedEntries)
        {
            if (expectedMissCids.Contains(pub.Cid!)) continue;

            if (channelExpectedGroups.TryGetValue(pub.Type ?? "", out var expectedGroups))
            {
                foreach (var expectedGroup in expectedGroups)
                {
                    var key = $"{pub.Cid}:{expectedGroup}";
                    // Check if received under group name or under any node in that role
                    var hasReceipt = receivedMap.Contains(key) || receivedEntries.Any(r => r.Cid == pub.Cid && (r.Group == expectedGroup || r.App == expectedGroup || r.Node.StartsWith(expectedGroup, StringComparison.OrdinalIgnoreCase)));
                    if (!hasReceipt)
                    {
                        missingPublishes.Add((pub.Cid!, pub.Type ?? "", expectedGroup));
                    }
                }
            }
        }

        var missingCids = missingPublishes.Select(m => m.Cid).Distinct().ToList();

        var inv = new InvariantResult
        {
            Name = "I4_PubSubPerLiveGroup",
            Description = "Every published event reaches each subscription group live at publish time",
            SentCount = publishedEntries.Count,
            ProcessedCount = receivedEntries.Count,
            ExpectedMissCount = expectedMissCids.Count,
            Passed = missingCids.Count == 0,
            Verdict = missingCids.Count == 0 ? (expectedMissCids.Count > 0 ? "PASS_WITH_NOTES" : "PASS") : "FAIL"
        };

        if (missingCids.Count > 0)
        {
            inv.OffendingCids = missingCids.Take(20).ToList();
            inv.Notes = $"{missingCids.Count} publish event(s) missed by registered subscription groups.";
            foreach (var cid in inv.OffendingCids)
            {
                if (rawLinesByCid.TryGetValue(cid, out var lines))
                {
                    inv.OffendingLedgerLines[cid] = lines;
                }
            }
        }
        else
        {
            inv.Notes = $"All {publishedEntries.Count} published events reached expected subscription groups ({expectedMissCids.Count} expected misses accounted for).";
        }

        result.Invariants[inv.Name] = inv;
    }

    private static void EvaluateI5Duplicates(List<LedgerEntry> entries, ReconciliationResult result)
    {
        var processedEntries = entries.Where(e => e.Kind == "processed" && !string.IsNullOrEmpty(e.Cid)).ToList();
        var totalProcessed = processedEntries.Count;
        var distinctProcessed = processedEntries.Select(e => e.Cid!).Distinct(StringComparer.Ordinal).Count();
        var dupCount = totalProcessed - distinctProcessed;

        var inv = new InvariantResult
        {
            Name = "I5_Duplicates",
            Description = "Duplicates counted and attributed (at-least-once guarantee; redeliveries expected around kills)",
            ProcessedCount = totalProcessed,
            DuplicateCount = dupCount,
            Passed = true, // Duplicates are counted, never fail the run (OD6)
            Verdict = dupCount > 0 ? "PASS_WITH_NOTES" : "PASS",
            Notes = $"Observed {dupCount} duplicate delivery(s) out of {totalProcessed} processed events ({(totalProcessed > 0 ? (dupCount * 100.0 / totalProcessed).ToString("0.00") : "0.00")} %)."
        };

        result.Invariants[inv.Name] = inv;
    }

    private static async Task EvaluateI6DeadLettersAsync(string runDir, ReconciliationResult result, CancellationToken ct)
    {
        var dlqFile = Path.Combine(runDir, "broker", "dlq.json");
        var dlqCount = 0;
        var dlqNotes = "Zero dead letters in broker DLQ.";

        if (File.Exists(dlqFile))
        {
            try
            {
                var text = await File.ReadAllTextAsync(dlqFile, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    dlqCount = doc.RootElement.GetArrayLength();
                }
                else if (doc.RootElement.TryGetProperty("count", out var countProp))
                {
                    dlqCount = countProp.GetInt32();
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to parse DLQ file: {ex.Message}");
            }
        }

        var inv = new InvariantResult
        {
            Name = "I6_DeadLetters",
            Description = "Zero dead letters in HW.DLQ, or all entries listed with reason",
            ProcessedCount = dlqCount,
            Passed = dlqCount == 0,
            Verdict = dlqCount == 0 ? "PASS" : "FAIL",
            Notes = dlqCount == 0 ? dlqNotes : $"{dlqCount} dead letter(s) found in DLQ."
        };

        result.Invariants[inv.Name] = inv;
    }

    private static async Task EvaluateI7NothingLeftBehindAsync(string runDir, ReconciliationResult result, CancellationToken ct)
    {
        var statsFile = Path.Combine(runDir, "broker", "stats-samples.jsonl");
        var finalDepth = 0;
        var passed = true;
        var notes = "Final queue depths are 0.";

        if (File.Exists(statsFile))
        {
            var lines = new List<string>();
            using (var fs = new FileStream(statsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8))
            {
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
                }
            }

            if (lines.Count > 0)
            {
                var lastLine = lines[^1];
                try
                {
                    using var doc = JsonDocument.Parse(lastLine);
                    if (doc.RootElement.TryGetProperty("queues", out var queuesProp))
                    {
                        foreach (var q in queuesProp.EnumerateObject())
                        {
                            if (q.Value.TryGetProperty("depth", out var dProp))
                            {
                                var d = dProp.GetInt32();
                                finalDepth += d;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Failed to parse stats samples: {ex.Message}");
                }
            }
        }

        if (finalDepth > 0)
        {
            passed = false;
            notes = $"Final queue depth is {finalDepth} (expected 0).";
        }

        var inv = new InvariantResult
        {
            Name = "I7_NothingLeftBehind",
            Description = "After drain phase, all queue depths reach zero",
            ProcessedCount = finalDepth,
            Passed = passed,
            Verdict = passed ? "PASS" : "FAIL",
            Notes = notes
        };

        result.Invariants[inv.Name] = inv;
    }

    private static string GenerateMarkdownReport(ReconciliationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Assurance Rig Reconciliation Report: {result.RunId}");
        sb.AppendLine();
        sb.AppendLine($"**Timestamp:** {result.Timestamp}  ");
        sb.AppendLine($"**Overall Verdict:** `{result.Verdict}` (Exit Code: {result.ExitCode})  ");
        sb.AppendLine();

        sb.AppendLine("## Invariants Summary");
        sb.AppendLine();
        sb.AppendLine("| Invariant | Verdict | Sent/Total | Processed | Notes |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var (name, inv) in result.Invariants)
        {
            var badge = inv.Passed ? (inv.Verdict == "PASS_WITH_NOTES" ? "🟡 PASS (NOTES)" : "🟢 PASS") : "🔴 FAIL";
            sb.AppendLine($"| **{name}** | {badge} | {inv.SentCount} | {inv.ProcessedCount} | {inv.Notes} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Event Counts");
        sb.AppendLine();
        sb.AppendLine("| Kind | Count |");
        sb.AppendLine("|---|---|");
        foreach (var (kind, count) in result.TotalEventsByKind.OrderBy(k => k.Key))
        {
            sb.AppendLine($"| `{kind}` | {count} |");
        }

        sb.AppendLine();
        sb.AppendLine("| Type | Count |");
        sb.AppendLine("|---|---|");
        foreach (var (type, count) in result.TotalEventsByType.OrderBy(t => t.Key))
        {
            sb.AppendLine($"| `{type}` | {count} |");
        }

        if (result.Invariants.Values.Any(i => i.OffendingCids.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine("## Offending Items & Ledger Traces");
            sb.AppendLine();

            foreach (var inv in result.Invariants.Values.Where(i => i.OffendingCids.Count > 0))
            {
                sb.AppendLine($"### {inv.Name}");
                sb.AppendLine();
                foreach (var cid in inv.OffendingCids)
                {
                    sb.AppendLine($"- **CID:** `{cid}`");
                    if (inv.OffendingLedgerLines.TryGetValue(cid, out var lines))
                    {
                        sb.AppendLine("  ```jsonl");
                        foreach (var l in lines)
                        {
                            sb.AppendLine($"  {l}");
                        }
                        sb.AppendLine("  ```");
                    }
                }
            }
        }

        return sb.ToString();
    }
}
