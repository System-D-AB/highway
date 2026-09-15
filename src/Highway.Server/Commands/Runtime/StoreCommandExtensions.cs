using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Runtime;

/// <summary>
/// Command-layer helpers over <see cref="IHighwayStore"/> that reproduce the behaviour the
/// old <c>HighwayCommandBase</c> provided (byte accounting, counter reads). These sit on the
/// command side, not the seam — the seam stays the minimal primitive set (038); anything
/// command-specific (like "delete the counter key when it reaches zero") lives here.
/// </summary>
internal static class StoreCommandExtensions
{
    /// <summary>
    /// Reads a byte counter (feature 016). Absent or unset reads as zero.
    /// Counters live in the <c>n</c> family via <see cref="IHighwayStore.Increment"/>, so the
    /// value is an i64; a missing key is zero.
    /// </summary>
    public static long ReadByteCounter(this IHighwayStore store, IStoreSnapshot snapshot, string counterName)
    {
        var key = HighwayKeyspace.Counter(counterName);
        var raw = store.Get(snapshot, key);
        return raw is { Length: >= 8 }
            ? System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(raw)
            : 0;
    }

    /// <summary>
    /// Adjusts a byte counter by <paramref name="delta"/> inside <paramref name="batch"/>,
    /// deleting the key when it reaches zero so an idle queue leaves nothing behind — the
    /// old <c>AdjustByteCounter</c> semantics (clamp at zero, delete-when-zero).
    ///
    /// <para>Reads through the batch (read-your-own-writes) so repeated adjusts in one
    /// command compose correctly.</para>
    /// </summary>
    public static void AdjustByteCounter(this IHighwayStore store, IStoreBatch batch, string counterName, long delta)
    {
        var key = HighwayKeyspace.Counter(counterName);
        // Increment returns the new value; it reads-through the batch overlay (B1-safe under
        // the per-name lock the command already holds).
        var updated = store.Increment(batch, key, delta);
        if (updated <= 0)
            store.Delete(batch, key);
    }
}
