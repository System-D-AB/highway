using RocksDbSharp;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// Applies a primary WAL page onto a replica (042 T2v).
///
/// <para><b>Mechanism (a) — derived watermark.</b> After a durable ingest, the applied
/// watermark <i>is</i> <see cref="RocksDb.GetLatestSequenceNumber"/>. There is no second
/// <c>Put(sys|repl|watermark)</c>, so there is no apply-vs-watermark gap to crash between.
/// A re-pulled page whose last sequence is ≤ that number is skipped whole.</para>
///
/// <para><b>Durability.</b> Toolkit <c>ReplicationConsumer.IngestBatch</c> writes with the
/// default (unsynced) <c>WriteOptions</c>. Highway's replica must match 038 sync-per-commit,
/// so this type writes the same batch bytes with <c>SetSync(true)</c>. The sequence number
/// argument on <c>IngestBatch(seq, data)</c> is unused by the binding — sequence lives in
/// the batch bytes — and is used here only for the skip check.</para>
/// </summary>
internal static class ReplicationApply
{
    private static readonly WriteOptions Sync = new WriteOptions().SetSync(true);

    /// <summary>The replica's applied watermark: the DB sequence of the last ingested batch.</summary>
    public static ulong Watermark(RocksDb db) => db.GetLatestSequenceNumber();

    /// <summary>
    /// Ingest <paramref name="data"/> if <paramref name="sequenceNumber"/> is past the
    /// current watermark. Returns <see langword="false"/> when the page is a re-pull
    /// (no write, no double-apply).
    /// </summary>
    public static bool TryIngest(RocksDb db, ulong sequenceNumber, ReadOnlySpan<byte> data)
    {
        if (sequenceNumber <= db.GetLatestSequenceNumber())
            return false;

        using var batch = new WriteBatch(data.ToArray());
        db.Write(batch, Sync);
        return true;
    }
}
