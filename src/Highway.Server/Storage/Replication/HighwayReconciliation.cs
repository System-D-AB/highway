using System.Text;
using System.Text.Json;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Crystal;

namespace Highway.Server.Storage.Replication;

/// <summary>
/// The reconciliation report a demoted node writes (042 G9 / R4.2), in Highway's terms. Crystal writes the facts — per
/// database, the positions a standby acknowledged and this node reached, and per box what the unreplicated tail put and
/// deleted (JSON, under <c>replication/reconciliation/</c>). This names each box as the operator knows it — "queue
/// orders", "service prices" — from Highway's catalogue, and keeps the report's wording, so an operator replays by
/// queue, deliberately. The raw tail stays in the data directory; the broker never merges.
/// </summary>
internal static class HighwayReconciliation
{
    public static string Write(CrystalHighwayStore store, ulong epoch, string? crystalReport, DateTimeOffset now)
    {
        var dir = Path.Combine(store.DataDir, "replication", "reconciliation");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"reconciliation-e{epoch}-{now.UtcTicks}.txt");

        long replicated = 0, local = 0;
        var gap = false;
        var lines = new List<string>();
        if (crystalReport is not null && File.Exists(crystalReport))
        {
            var uid = store.Crystal.ListDatabases().FirstOrDefault(d => d.Name == CrystalHighwayStore.DatabaseName)?.Uid;
            using var doc = JsonDocument.Parse(File.ReadAllText(crystalReport));
            foreach (var db in doc.RootElement.GetProperty("databases").EnumerateArray())
            {
                if (uid is not null && db.GetProperty("uid").GetString() != uid) continue;
                replicated = db.GetProperty("lastReplicatedSeq").GetInt64();
                local = db.GetProperty("lastLocalSeq").GetInt64();
                gap = db.GetProperty("gap").GetBoolean();
                foreach (var box in db.GetProperty("boxes").EnumerateArray())
                {
                    var name = box.GetProperty("name").GetString() ?? "";
                    var entity = CrystalBoxMap.TryParse(name, out var id) ? EntityOf(store, id) : name;
                    lines.Add($"{entity}: puts={box.GetProperty("puts").GetInt64()} bytes={box.GetProperty("putBytes").GetInt64()} " +
                              $"deletes={box.GetProperty("deletes").GetInt64()}");
                }
                var undecoded = db.GetProperty("undecodedBatches").GetInt32();
                if (undecoded > 0)
                    lines.Add($"undecoded-batches={undecoded} (raw WAL preserved)");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"epoch={epoch}");
        sb.AppendLine($"lastReplicatedSeq={replicated}");
        sb.AppendLine($"lastLocalSeq={local}");
        sb.AppendLine();
        if (gap)
            sb.AppendLine("tail-undecodable: the write-ahead log no longer reaches back to the last replicated position — the tail is preserved in the data directory, not lost");
        else if (local > replicated && lines.Count > 0)
        {
            sb.AppendLine($"unreplicated tail ({replicated + 1}..{local}), by entity:");
            foreach (var line in lines.Order(StringComparer.Ordinal))
                sb.AppendLine("  " + line);
        }
        else
            sb.AppendLine("no unreplicated tail: every local write had been acked by a replica.");
        sb.AppendLine();
        sb.AppendLine("tail is preserved; operator replay only — the broker never merges.");
        if (crystalReport is not null)
            sb.AppendLine($"storage report: {crystalReport}");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>A work box's "kind name" from the catalogue (065), "system" for box 0, or the box id when unknown.</summary>
    private static string EntityOf(CrystalHighwayStore store, uint box)
    {
        if (box == BoxKeys.SystemBox) return "system";
        try
        {
            using var snapshot = store.Snapshot();
            var raw = store.Get(snapshot, BoxKeys.MetaKey(box));
            if (raw is null) return $"box {box}";
            using var doc = JsonDocument.Parse(raw);
            var name = doc.RootElement.GetProperty("name").GetString();
            var kind = doc.RootElement.GetProperty("kind").GetInt32() switch
            {
                (int)BoxKeys.Kind.Service => "service",
                (int)BoxKeys.Kind.DeadFeed => "dead-letter feed",
                _ => "queue",
            };
            return $"{kind} {name}";
        }
        catch (Exception)
        {
            return $"box {box}";   // the report still names the box by id
        }
    }
}
