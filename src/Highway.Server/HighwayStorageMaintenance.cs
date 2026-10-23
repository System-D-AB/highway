namespace Highway.Server;

/// <summary>The outcome of an offline compaction.</summary>
/// <param name="BytesBefore">Size of the data directory before compacting.</param>
/// <param name="BytesAfter">Size after.</param>
/// <param name="Elapsed">How long the compaction took.</param>
public sealed record CompactionResult(long BytesBefore, long BytesAfter, TimeSpan Elapsed);

/// <summary>
/// Offline maintenance of a broker's data directory (feature 061; on SystemD.Crystal since 076 R3.3). Run with the
/// broker <b>stopped</b>: Crystal's store lock refuses a second opener, so running this against a live broker fails
/// cleanly rather than corrupting anything.
/// </summary>
public static class HighwayStorageMaintenance
{
    /// <summary>
    /// <c>highways --compact</c> (feature 061; on Crystal's offline compaction since Crystal 1.0.0-rc.3): compacts every
    /// database of the stopped broker's store, dropping every tombstone. It changes no data. Refused — and nothing
    /// touched — while a broker holds the store, or for a directory that is not a Crystal store.
    /// </summary>
    public static CompactionResult Compact(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        if (!File.Exists(Path.Combine(dataDir, "crystal.format")))
            throw new InvalidOperationException($"'{dataDir}' is not a Highway data directory (no Crystal store in it).");

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = SystemD.Crystal.CrystalStore.Compact(dataDir);   // refused while the store is open anywhere
        return new CompactionResult(
            result.Databases.Sum(d => d.BytesBefore),
            result.Databases.Sum(d => d.BytesAfter),
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// <c>highways --wipe-data</c> (065 T6): empties a data directory for a fresh start. A Crystal store is reset —
    /// its databases go, its node identity and epoch stay, so the node keeps its place in its replica set. A directory
    /// an earlier storage engine wrote is emptied entirely (its replication files mean nothing to this release).
    /// Refuses while a broker holds the directory. Returns how many entries were removed.
    /// </summary>
    public static int WipeData(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        if (!Directory.Exists(dataDir)) return 0;

        if (File.Exists(Path.Combine(dataDir, "crystal.format")))
        {
            var before = Directory.EnumerateFileSystemEntries(dataDir, "*", SearchOption.AllDirectories).Count();
            SystemD.Crystal.CrystalStore.Reset(dataDir);   // refused while a broker holds the store
            return Math.Max(0, before - Directory.EnumerateFileSystemEntries(dataDir, "*", SearchOption.AllDirectories).Count());
        }

        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
            removed++;
        }
        return removed;
    }
}
