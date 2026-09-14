using System;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace Stow.Storage.Engine;

/// <summary>
/// The three sequence identities, kept apart deliberately.
///
/// V8 states that one <em>ordering</em> is the spine. It is not one <em>variable</em>:
/// a document revision, a snapshot identity and a replication position share that ordering
/// but have different requirements, and conflating them was defect B1.
///
/// See the V8 amendment (2026-08-14) in <c>2026-08-13-v2-decisions.md</c>.
/// </summary>
internal static class SequenceSource
{
    // RocksDbSharp exposes Snapshot.Handle but no sequence accessor. The C API function is
    // exported by the shipped native library (verified: probe section (k)), so this needs a
    // P/Invoke rather than a binding contribution.
    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong rocksdb_snapshot_get_sequence_number(IntPtr snapshot);

    /// <summary>
    /// The <b>exact</b> sequence a snapshot reads at.
    ///
    /// This is what <c>asOf</c> must be. Reading <c>GetLatestSequenceNumber()</c> after
    /// creating a snapshot returns a value that may be <em>newer</em> than what the snapshot
    /// actually sees, which would make the reported <c>asOf</c> describe a view the caller
    /// never got.
    /// </summary>
    public static long ExactSnapshotSequence(Snapshot snapshot)
        => (long)rocksdb_snapshot_get_sequence_number(snapshot.Handle);

    /// <summary>
    /// A <b>safe lower bound</b> on the engine position after a write has committed —
    /// what a caller passes back as <c>Stow-Min-Sequence</c> for read-your-writes.
    ///
    /// Imprecision is harmless here and only in this direction: a value at or after the
    /// caller's own write is exactly the guarantee needed, and being slightly high merely
    /// makes a replica wait a little longer. The same imprecision would be fatal for a
    /// document revision, which is why the two are separate calls.
    ///
    /// Must be read <b>after</b> <c>db.Write(batch)</c> returns.
    /// </summary>
    public static long EngineSequenceAfterCommit(RocksDb db)
        => (long)db.GetLatestSequenceNumber();
}
