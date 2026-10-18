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
    /// Refused since 076: Crystal compacts delete-heavy boxes on its own, and does not yet offer an offline compaction
    /// of a whole store (the feature this command needs). The data directory is untouched.
    /// </summary>
    public static CompactionResult Compact(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        throw new NotSupportedException(
            "'highways --compact' is not available in this release: the storage engine (SystemD.Crystal) compacts " +
            "delete-heavy data on its own and does not yet offer an offline compaction. Nothing was changed.");
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
