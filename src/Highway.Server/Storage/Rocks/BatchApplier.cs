using System.Buffers.Binary;
using RocksDbSharp;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// Replica-side apply of a <c>HW.REPL.PULL</c> page (042 T2).
///
/// <para>Toolkit apply is <see cref="ReplicationConsumer"/>; Highway does not call
/// <c>IngestBatch</c> as-is because it is unsynced (T2v). Each batch is ingested through
/// <see cref="ReplicationApply"/> — sync write, watermark = <c>GetLatestSequenceNumber()</c>,
/// skip-by-sequence on re-pull.</para>
///
/// <para><b>One synced write per page (066 C2).</b> A page's batches are contiguous, so their records
/// are concatenated into one batch — header at the first sequence, count the sum — and written with a
/// single fsync. The replica's sequence advances exactly as it would batch by batch, and a crash leaves
/// either the whole page or none of it. A page that is not a clean contiguous run falls back to one
/// write per batch.</para>
///
/// <para><b>Epoch refusal (R1.4).</b> The page carries the primary's epoch. A replica that
/// has already seen a higher epoch refuses the whole page and writes nothing.</para>
/// </summary>
internal sealed class BatchApplier
{
    private readonly RocksDb _db;

    public BatchApplier(RocksDb db, ulong initialEpoch = 0)
    {
        _db = db;
        SeenEpoch = initialEpoch;
        Consumer = new ReplicationConsumer(db);
    }

    /// <summary>Toolkit consumer this applier wraps. Ingest goes through <see cref="ReplicationApply"/> (sync).</summary>
    internal ReplicationConsumer Consumer { get; }

    /// <summary>
    /// Highest primary epoch this replica has accepted. Seeded from the feeder's
    /// persisted epoch (042 G2) so a replica restart cannot be fed lower-epoch pages
    /// by a stale resurrected primary.
    /// </summary>
    public ulong SeenEpoch { get; private set; }

    /// <summary>Derived watermark — last ingested sequence, or 0 on a blank replica.</summary>
    public ulong Watermark => ReplicationApply.Watermark(_db);

    /// <summary>
    /// Apply one pull page. Returns <see cref="ApplyStatus.EpochRefused"/> without writing
    /// when <paramref name="epoch"/> is below <see cref="SeenEpoch"/>.
    /// </summary>
    public ApplyResult ApplyPage(ulong epoch, IReadOnlyList<(ulong Seq, byte[] Data)> batches)
    {
        if (epoch < SeenEpoch)
            return ApplyResult.Refused(SeenEpoch);

        if (epoch > SeenEpoch)
            SeenEpoch = epoch;

        var watermark = Watermark;
        var fresh = new List<byte[]>(batches.Count);
        foreach (var (seq, data) in batches)
        {
            if (seq > watermark)
                fresh.Add(data);
        }

        if (fresh.Count > 1 && TryConcat(fresh, out var page))
        {
            ReplicationApply.Write(_db, page);
            SyncedWrites++;
            return ApplyResult.Ok(fresh.Count, batches.Count - fresh.Count, Watermark, SeenEpoch);
        }

        var applied = 0;
        var skipped = 0;
        foreach (var (seq, data) in batches)
        {
            if (ReplicationApply.TryIngest(_db, seq, data))
            {
                applied++;
                SyncedWrites++;
            }
            else
                skipped++;
        }

        return ApplyResult.Ok(applied, skipped, Watermark, SeenEpoch);
    }

    /// <summary>Synced writes issued so far (066: one per page on the contiguous path).</summary>
    internal int SyncedWrites { get; private set; }

    /// <summary>
    /// Concatenates serialized write batches into one (066 C2): header <c>[8B LE first sequence][4B LE
    /// total count]</c>, then every batch's records in order. False unless each batch starts exactly where
    /// the previous one ended (<c>seq + count</c>) — anything else is applied batch by batch, unchanged.
    /// </summary>
    internal static bool TryConcat(IReadOnlyList<byte[]> batches, out byte[] merged)
    {
        const int header = 12;
        merged = [];
        if (batches.Count == 0) return false;

        ulong first = 0, next = 0, total = 0;
        var bytes = header;
        for (var i = 0; i < batches.Count; i++)
        {
            var b = batches[i];
            if (b.Length < header) return false;
            var seq = BinaryPrimitives.ReadUInt64LittleEndian(b);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
            if (i == 0) first = seq;
            else if (seq != next) return false;
            next = seq + count;
            total += count;
            bytes += b.Length - header;
        }
        if (total > uint.MaxValue) return false;

        merged = new byte[bytes];
        BinaryPrimitives.WriteUInt64LittleEndian(merged, first);
        BinaryPrimitives.WriteUInt32LittleEndian(merged.AsSpan(8), (uint)total);
        var pos = header;
        foreach (var b in batches)
        {
            b.AsSpan(header).CopyTo(merged.AsSpan(pos));
            pos += b.Length - header;
        }
        return true;
    }
}

internal readonly record struct ApplyResult(
    ApplyStatus Status,
    int Applied,
    int Skipped,
    ulong Watermark,
    ulong SeenEpoch)
{
    public static ApplyResult Ok(int applied, int skipped, ulong watermark, ulong seenEpoch)
        => new(ApplyStatus.Applied, applied, skipped, watermark, seenEpoch);

    public static ApplyResult Refused(ulong seenEpoch)
        => new(ApplyStatus.EpochRefused, 0, 0, 0, seenEpoch);
}

internal enum ApplyStatus
{
    Applied,
    EpochRefused,
}
