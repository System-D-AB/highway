using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// Shared consumer machinery ported from <c>HighwayCommandBase.LeaseSweep</c> and
/// <c>HwQClaim.PromoteDueMessages</c> onto <see cref="IHighwayStore"/> (039 T4). Both
/// <c>HW.DEQUEUE</c> and <c>HW.QCLAIM</c> run these on every claim, and the drain family
/// (T5) reuses the sweep's decode/encode shape.
///
/// <para>All time comparisons take <c>nowTicks</c> — the clock read once by the command
/// (037 R5.1). The sweep persists the <b>rows it produced</b> (requeued entries, dead-letter
/// records), never an instruction to sweep (037 R5.2): every write is staged into the caller's
/// batch, committed once.</para>
/// </summary>
internal static class ConsumerSupport
{
    /// <summary>One dead-lettered entry, for the command's post-commit recorder.</summary>
    internal readonly record struct SweepOutcome(string Id, ushort Attempts);

    internal delegate void EntryDecoder(ReadOnlySpan<byte> data, out long claimTicks, out byte[] id, out byte[] payload, out ushort attempts);
    internal delegate byte[] QueueEntryEncoder(byte[] id, byte[] payload, ushort attempts);

    /// <summary>
    /// Moves matured delayed messages into the live queue: range the delayed set by score up
    /// to <paramref name="nowTicks"/>, tail-push each into the live queue, remove it from the
    /// set — range-read-then-remove, never pop-and-restore (a gap would lose anything not yet
    /// due). All in the caller's <paramref name="batch"/>.
    /// </summary>
    public static void PromoteDueMessages(
        IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch, string queueName, long nowTicks, int limit = 256)
    {
        var delayedKey = HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed(queueName));
        var due = store.SortedSetRangeByScore(snap, delayedKey, long.MinValue, nowTicks, limit);

        foreach (var member in due)
        {
            HwQSendCommand.PushTail(store, batch, HighwayNames.Queue(queueName), member);
            store.SortedSetRemove(batch, delayedKey, member);
        }
    }

    /// <summary>
    /// Sweeps one processing list: drain it, keep entries whose lease has not expired, requeue
    /// or dead-letter the rest with the attempt count carried forward. Returns what was
    /// dead-lettered (for the recorder). Ported from <c>SweepExpiredEntries</c>; the
    /// drain/filter/restore is now <see cref="IHighwayStore.ListDrain"/> + re-push.
    /// </summary>
    /// <param name="returnToHead">Pub/Sub returns a survivor to the head (keeps its place); RPC to the tail.</param>
    /// <param name="bytesCounterName">When set, requeued bytes go back onto this queue's counter (016).</param>
    public static List<SweepOutcome> SweepExpiredEntries(
        IHighwayStore store,
        IStoreBatch batch,
        string procListName,
        string liveQueueName,
        string dlqName,
        long leaseExpiry,
        long nowTicks,
        HighwayServerOptions opts,
        EntryDecoder decode,
        QueueEntryEncoder encodeQueueEntry,
        Func<byte[], string> idToString,
        bool returnToHead = false,
        string? bytesCounterName = null)
    {
        var deadLettered = new List<SweepOutcome>();

        // 061 A2: read-only. Only an entry whose lease has expired is touched (deleted, then requeued or
        // dead-lettered); an entry still within its lease is left exactly where it is. The previous
        // drain-and-restore deleted and re-wrote every in-flight entry on every poll, and the tombstones
        // it left made every later scan slower. A sweep that finds
        // nothing expired now stages nothing, so the claim that ran it commits nothing (059).
        foreach (var (key, entry) in store.ListEntries(batch, HighwayKeyspace.ListPrefix(procListName)))
        {
            var span = (ReadOnlySpan<byte>)entry;

            if (Envelope.IsLegacyEntry(span))
                throw new StorageFormatException(procListName);

            decode(span, out var claimTicks, out var id, out var payload, out var attempts);

            if (claimTicks >= leaseExpiry)
                continue; // lease still valid — untouched

            store.Delete(batch, key);

            var next = Envelope.NextAttempt(attempts);
            var source = entry;

            if (opts.MaxDeliveryAttempts > 0 && next > opts.MaxDeliveryAttempts)
            {
                var original = Envelope.CarryFailureBlock(source, encodeQueueEntry(id, payload, next));
                var dead = DeadLetter.Encode(nowTicks, next, DeadLetter.MaxAttempts, original);
                HwQSendCommand.PushTail(store, batch, dlqName, dead);
                TrimDeadLetters(store, batch, dlqName, opts.MaxDeadLetterEntries);
                deadLettered.Add(new SweepOutcome(idToString(id), next));
                continue;
            }

            var requeued = Envelope.CarryFailureBlock(source, encodeQueueEntry(id, payload, next));

            if (bytesCounterName is not null)
                store.AdjustByteCounter(batch, bytesCounterName, requeued.Length);

            if (returnToHead)
                PushHead(store, batch, liveQueueName, requeued);
            else
                HwQSendCommand.PushTail(store, batch, liveQueueName, requeued);
        }

        return deadLettered;
    }

    /// <summary>
    /// Head-push (redeliver-to-head, scheme 1): reserve the next-lower seq from the list's
    /// low-water counter and write there, so it pops before existing tail entries
    /// (physical-layout §5).
    /// </summary>
    public static void PushHead(IHighwayStore store, IStoreBatch batch, string listName, byte[] value)
    {
        var lowWater = HighwayKeyspace.Counter(HighwayNames.ListSequence(listName) + ":low");
        var seq = store.Increment(batch, lowWater, -1); // -1, -2, … always below tail (>= 0)
        store.ListLeftPush(batch, HighwayKeyspace.ListEntry(listName, seq), value);
    }

    /// <summary>
    /// The ack/fail/touch family's one idiom (061 A1): offer each processing-list entry, in FIFO order,
    /// to <paramref name="transform"/>, which returns the <b>same</b> entry to leave it alone,
    /// <c>null</c> to remove it, or new bytes to replace it. Only an entry that changed is written —
    /// removed with a delete of its own key, or replaced in place under that key, so its position is
    /// kept. Replaces drain-and-restore, which deleted and re-pushed every in-flight entry to change one.
    /// </summary>
    public static void UpdateInPlace(
        IHighwayStore store, IStoreBatch batch, string procListName, Func<byte[], byte[]?> transform)
    {
        foreach (var (key, entry) in store.ListEntries(batch, HighwayKeyspace.ListPrefix(procListName)))
        {
            var result = transform(entry);
            if (result is null)
                store.Delete(batch, key);
            else if (!ReferenceEquals(result, entry))
                store.Set(batch, key, result);
        }
    }

    /// <summary>Bounds a DLQ to <paramref name="maxEntries"/>, dropping the oldest (head) first.</summary>
    public static void TrimDeadLetters(IHighwayStore store, IStoreBatch batch, string dlqName, int maxEntries)
    {
        if (maxEntries <= 0) return;
        // 061 A1: delete only the overflow at the head (oldest first). This used to drain the whole DLQ
        // (up to MaxDeadLetterEntries, default 10 000) and re-push it, on every dead-lettered message.
        var entries = store.ListEntries(batch, HighwayKeyspace.ListPrefix(dlqName));
        for (var i = 0; i < entries.Count - maxEntries; i++)
            store.Delete(batch, entries[i].Key);
    }
}
