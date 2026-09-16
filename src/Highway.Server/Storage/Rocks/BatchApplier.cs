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

        var applied = 0;
        var skipped = 0;
        foreach (var (seq, data) in batches)
        {
            if (ReplicationApply.TryIngest(_db, seq, data))
                applied++;
            else
                skipped++;
        }

        return ApplyResult.Ok(applied, skipped, Watermark, SeenEpoch);
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
