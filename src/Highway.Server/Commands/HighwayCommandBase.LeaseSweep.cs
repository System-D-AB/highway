using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// The lease sweep, in one place (feature 014, extracted from feature 013).
///
/// <para>An entry claimed by a node that never acknowledged it must be recovered. That
/// recovery is where feature 013's delivery-attempt counting and dead-lettering live, and
/// it is <b>identical</b> for an RPC service queue and a work queue — the only differences
/// are which keys are involved and how the entry is framed.</para>
///
/// <para><b>Why this is shared rather than copied.</b> Feature 013 found the same
/// unbounded-redelivery defect living in three separate requeue paths, because each had
/// been written independently. A fourth copy for queues is exactly how that happens again.
/// The queue commands are thin subclasses over this.</para>
/// </summary>
internal abstract partial class HighwayCommandBase
{
    /// <summary>
    /// What the sweep decided for one expired entry, so the caller can record it.
    /// </summary>
    protected readonly record struct SweepOutcome(string Id, ushort Attempts);

    /// <summary>
    /// Moves expired entries out of one processing list: back to the queue, or to the
    /// dead-letter list once they have exhausted <c>MaxDeliveryAttempts</c>.
    ///
    /// <para>Everything happens inside the caller's transaction, so an entry is never in
    /// both the processing list and its destination, and never in neither.</para>
    /// </summary>
    /// <param name="api">The transactional API.</param>
    /// <param name="procKey">Processing list to sweep.</param>
    /// <param name="queueKey">Where a surviving entry is returned to.</param>
    /// <param name="dlqKey">Where an exhausted entry goes.</param>
    /// <param name="procKeyName">Name of the processing list, for the storage-format error.</param>
    /// <param name="leaseExpiry">Claim timestamps older than this are expired.</param>
    /// <param name="opts">Server options — attempt limit and dead-letter cap.</param>
    /// <param name="decode">Reads an entry: claim ticks, id bytes, payload, attempts.</param>
    /// <param name="encodeQueueEntry">Rebuilds a queue entry from id, payload and attempts.</param>
    /// <param name="idToString">Renders the id for the recorder.</param>
    /// <param name="returnToHead">
    /// Whether a surviving entry goes to the head rather than the tail. Pub/Sub returns to
    /// the head so a redelivery keeps its place; RPC returns to the tail.
    /// </param>
    /// <returns>Entries that were dead-lettered.</returns>
    protected List<SweepOutcome> SweepExpiredEntries<TGarnetApi>(
        TGarnetApi api,
        PinnedSpanByte procKey,
        PinnedSpanByte queueKey,
        PinnedSpanByte dlqKey,
        string procKeyName,
        long leaseExpiry,
        HighwayServerOptions opts,
        EntryDecoder decode,
        QueueEntryEncoder encodeQueueEntry,
        Func<byte[], string> idToString,
        bool returnToHead = false)
        where TGarnetApi : IGarnetApi
    {
        var deadLettered = new List<SweepOutcome>();

        var status = api.ListLeftPop(procKey, int.MaxValue, out var entries);
        if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
            return deadLettered;

        var keep = new List<byte[]>();

        foreach (var entry in entries)
        {
            var span = entry.ReadOnlySpan;

            // A pre-013 entry must be refused, never skipped and never misparsed: read as
            // a current entry it would reinterpret its leading bytes and hand a corrupt
            // payload to an application, which is worse than an error.
            if (Envelope.IsLegacyEntry(span))
                throw new StorageFormatException(procKeyName);

            decode(span, out var claimTicks, out var id, out var payload, out var attempts);

            if (claimTicks >= leaseExpiry)
            {
                keep.Add(span.ToArray());
                continue;
            }

            // Expired. This is the redelivery path that used to be unbounded: the entry
            // went back on the queue with nothing counting how often that had already
            // happened, so a permanently failing message was retried for the life of the
            // deployment — and, the queue being FIFO, retried ahead of everything behind it.
            var next = Envelope.NextAttempt(attempts);

            // The failure block (015) rides on the rebuilt entry. Read from the entry the sweep
            // is ALREADY decoding, so no extra read and no N+1 inside the transaction.
            var source = span.ToArray();

            if (opts.MaxDeliveryAttempts > 0 && next > opts.MaxDeliveryAttempts)
            {
                var original = Envelope.CarryFailureBlock(source, encodeQueueEntry(id, payload, next));

                var dead = DeadLetter.Encode(DateTime.UtcNow.Ticks, next, DeadLetter.MaxAttempts, original);

                api.ListRightPush(dlqKey, CreateArgSlice(dead), out _);
                TrimDeadLetters(api, dlqKey, opts.MaxDeadLetterEntries);

                deadLettered.Add(new SweepOutcome(idToString(id), next));
                continue;
            }

            // On the requeued entry too. Without this, `firstType` would be lost on the FIRST
            // redelivery and nothing would report it — the one silent failure mode in this
            // feature, which is why R4.4 calls it out.
            var revived = CreateArgSlice(
                Envelope.CarryFailureBlock(source, encodeQueueEntry(id, payload, next)));
            if (returnToHead)
                api.ListLeftPush(queueKey, revived, out _);
            else
                api.ListRightPush(queueKey, revived, out _);
        }

        // Restore entries whose lease has not expired.
        foreach (var e in keep)
            api.ListRightPush(procKey, CreateArgSlice(e), out _);

        return deadLettered;
    }

    /// <summary>Writes a null array (*-1\r\n) to <paramref name="output"/>.</summary>
    protected static void WriteNullArray(ref MemoryResult<byte> output)
    {
        const int len = 5;
        output.MemoryOwner?.Dispose();
        output.MemoryOwner = System.Buffers.MemoryPool<byte>.Shared.Rent(len);
        output.Length = len;
        var span = output.MemoryOwner.Memory.Span;
        span[0] = (byte)'*';
        span[1] = (byte)'-';
        span[2] = (byte)'1';
        span[3] = (byte)'\r';
        span[4] = (byte)'\n';
    }

    /// <summary>Reads a processing entry of whatever shape the caller uses.</summary>
    protected delegate void EntryDecoder(
        ReadOnlySpan<byte> data,
        out long claimTicks,
        out byte[] id,
        out byte[] payload,
        out ushort attempts);

    /// <summary>Rebuilds a queue entry from a decoded one.</summary>
    protected delegate byte[] QueueEntryEncoder(byte[] id, byte[] payload, ushort attempts);
}
