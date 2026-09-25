using System.Diagnostics;
using Highway.Server.Storage.Rocks;

namespace Highway.Server;

/// <summary>The outcome of an offline compaction.</summary>
/// <param name="BytesBefore">Size of the data directory before compacting.</param>
/// <param name="BytesAfter">Size after.</param>
/// <param name="Elapsed">How long the compaction took.</param>
public sealed record CompactionResult(long BytesBefore, long BytesAfter, TimeSpan Elapsed);

/// <summary>
/// Offline maintenance of a broker's data directory (feature 061). Run with the broker <b>stopped</b>:
/// RocksDB's own LOCK file refuses a second opener, so running this against a live broker fails cleanly
/// rather than corrupting anything.
/// </summary>
public static class HighwayStorageMaintenance
{
    /// <summary>
    /// Compacts the whole database, dropping every tombstone. A broker at 2.5.3 or earlier accumulated
    /// tombstones on its in-flight lists and queues, and every poll stepped over them (feature 061); this
    /// removes them at once. Safe to run any time the broker is stopped — it changes no data.
    /// </summary>
    public static CompactionResult Compact(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        if (!File.Exists(Path.Combine(dataDir, "CURRENT")))
            throw new InvalidOperationException($"'{dataDir}' is not a Highway data directory (no RocksDB CURRENT file).");

        var before = DirectorySize(dataDir);
        var stopwatch = Stopwatch.StartNew();
        using (var store = RocksDbStore.Open(dataDir))
            store.CompactAll();
        stopwatch.Stop();
        return new CompactionResult(before, DirectorySize(dataDir), stopwatch.Elapsed);
    }

    private static long DirectorySize(string path)
        => new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}
