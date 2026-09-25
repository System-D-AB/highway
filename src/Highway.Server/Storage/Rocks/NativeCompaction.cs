using RocksDbSharp;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// RocksDB's <c>CompactOnDeletionCollector</c> (061 T7). The table-properties collector marks an SST file
/// for compaction when any sliding window of <c>window</c> consecutive entries in it holds at least
/// <c>deletesTrigger</c> deletions (or a <c>ratio</c> of them) — so key ranges full of tombstones are
/// compacted away by RocksDB itself instead of waiting for size-triggered compaction, which on a small
/// broker database may never come. Queue pops, acks and lease sweeps are exactly such ranges.
///
/// <para><b>Not wrapped by the binding.</b> Verified against the assembly for the <c>RocksDB</c> package
/// 11.8.1.5032 (and the earlier 11.1.2.3412): <c>ColumnFamilyOptions</c> has no method for it, but the public
/// <c>Native</c> class declares <c>rocksdb_options_add_compact_on_deletion_collector_factory</c> and its
/// <c>_del_ratio</c> / <c>_min_file_size</c> variants. This calls the C entry point directly — the same
/// pattern as Crystal's <c>NativeApproximateSizes</c>. Recorded as a finding, not a fork.</para>
///
/// <para><b>It acts only on SST files</b>, so on its own it leaves the memtable alone — and a broker's
/// memtable fills slowly (small entries, 64 MiB buffer), so pops and acks can pile up hundreds of thousands
/// of tombstones there for hours. Any scan that starts below them (a queue-depth count, a delayed-set or
/// schedule scan) steps over all of them, and its cost grows with uptime.</para>
///
/// <para><b>Memtable scan flush trigger (061 T9).</b> RocksDB's <c>memtable_op_scan_flush_trigger</c>: when
/// one iterator operation (Seek/Next) steps over this many invisible entries — tombstones or overwritten
/// values — in the active memtable, the memtable is marked for flush. The flushed file then carries the
/// tombstones into the collector above, which compacts them away. Together they bound the tombstones any
/// scan can meet, whatever the scan: a fix for every unhinted read, including ones not yet found.
/// <c>memtable_avg_op_scan_flush_trigger</c> covers the interleaved case — many Next() calls each skipping a
/// few — once a window has skipped at least the per-op trigger in total. Also native-only in the binding.</para>
/// </summary>
internal static class NativeCompaction
{
    /// <summary>Sliding window, in entries, over which deletions are counted.</summary>
    internal const ulong DeletionWindow = 1024;

    /// <summary>Deletions within one window that mark the file for compaction.</summary>
    internal const ulong DeletionsTrigger = 512;

    /// <summary>Alternatively: fraction of the file's entries that are deletions.</summary>
    internal const double DeletionRatio = 0.5;

    /// <summary>Invisible memtable entries one Seek/Next may step over before the memtable is flushed.</summary>
    internal const uint MemtableScanFlushTrigger = 1000;

    /// <summary>Average invisible entries per Next() (over a window of at least the trigger) that flushes.</summary>
    internal const uint MemtableAvgScanFlushTrigger = 16;

    public static ColumnFamilyOptions CompactOnDeletions(this ColumnFamilyOptions family)
    {
        ArgumentNullException.ThrowIfNull(family);
        Native.Instance.rocksdb_options_add_compact_on_deletion_collector_factory_del_ratio(
            family.Handle, (UIntPtr)DeletionWindow, (UIntPtr)DeletionsTrigger, DeletionRatio);
        Native.Instance.rocksdb_options_set_memtable_op_scan_flush_trigger(family.Handle, MemtableScanFlushTrigger);
        Native.Instance.rocksdb_options_set_memtable_avg_op_scan_flush_trigger(family.Handle, MemtableAvgScanFlushTrigger);
        GC.KeepAlive(family);
        return family;
    }
}
