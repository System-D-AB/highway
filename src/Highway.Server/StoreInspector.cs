using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server;

/// <summary>
/// Read-only state inspection for integration tests (feature 040, the fixture swap).
///
/// <para>Under Garnet the integration suite asserted broker state with raw Redis reads
/// (<c>LLEN hw:q:…</c>, <c>SMEMBERS</c>, <c>GET</c> on counters). The 040 RESP server
/// deliberately serves only the <c>HW.*</c> subset (037 R6.3), and in-process the store is
/// right here — so state assertions read the store directly instead of going over a wire
/// that no longer speaks raw Redis.</para>
///
/// <para><b>Accepts the old Garnet key spellings.</b> <c>hw:q:invoices:q</c> means what it
/// always meant; the <c>hw:</c> prefix is stripped to the logical name
/// (<see cref="HighwayNames"/> — the direct translation table). Tests therefore swap
/// <c>db.Execute("LLEN", key)</c> for <c>server.Inspect.ListLength(key)</c> one-for-one.
/// The retired mirror keys (<c>:grplist</c>, <c>:nodelist</c>, <c>job:index</c> as a KV
/// string, …) do NOT translate — they no longer exist (physical-layout.md §3); a test that
/// asserted a mirror asserts the surviving set instead.</para>
///
/// <para><b>Built entirely on <see cref="IHighwayStore"/></b> — no engine type, no store
/// internals (Gate G1 untouched). Non-destructive list reads stage a drain into a batch
/// that is disposed uncommitted; counter reads use a zero-delta increment the same way.</para>
/// </summary>
internal sealed class StoreInspector(IHighwayStore store)
{
    private static string Name(string legacyKey)
        => legacyKey.StartsWith("hw:", StringComparison.Ordinal) ? legacyKey[3..] : legacyKey;

    /// <summary>LLEN. The number of entries in a list (queue / processing / DLQ).</summary>
    public long ListLength(string legacyListKey)
    {
        using var snap = store.Snapshot();
        return store.ListLength(snap, HighwayKeyspace.ListPrefix(Name(legacyListKey)));
    }

    /// <summary>LRANGE 0 -1, non-destructively: drain into a batch that is never committed.</summary>
    public IReadOnlyList<byte[]> ListEntries(string legacyListKey)
    {
        using var batch = store.NewBatch();
        return store.ListDrain(batch, HighwayKeyspace.ListPrefix(Name(legacyListKey)));
        // batch disposed uncommitted — the staged deletes never happen.
    }

    /// <summary>GET on a counter key (byte budgets, channel seq). Absent counters read 0.</summary>
    public long Counter(string legacyCounterKey)
    {
        using var batch = store.NewBatch();
        return store.Increment(batch, HighwayKeyspace.Counter(Name(legacyCounterKey)), 0);
        // zero-delta read; batch disposed uncommitted.
    }

    /// <summary>SMEMBERS, as UTF-8 strings. Works for any membership set (family s).</summary>
    public IReadOnlyList<string> SetMembers(string legacySetKey)
    {
        using var snap = store.Snapshot();
        return store.SetMembers(snap, HighwayKeyspace.SetPrefix(Name(legacySetKey)))
            .Select(m => System.Text.Encoding.UTF8.GetString(m))
            .ToList();
    }

    /// <summary>ZCARD on an ordered set (delayed set, job schedules).</summary>
    public long SortedSetCount(string legacyZsetKey)
    {
        using var snap = store.Snapshot();
        return store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(Name(legacyZsetKey)));
    }

    /// <summary>EXISTS on the RPC reply slot, honouring its expiry (OD5 semantics).</summary>
    public bool ReplySlotExists(string requestId, long? nowTicks = null)
    {
        using var snap = store.Snapshot();
        return store.GetLive(
            snap,
            HighwayKeyspace.Kv(HighwayNames.ReplySlot(requestId)),
            nowTicks ?? DateTime.UtcNow.Ticks) is not null;
    }

    /// <summary>Raw GET on a non-expiring KV key (registration records).</summary>
    public byte[]? KvGet(string legacyKvKey)
    {
        using var snap = store.Snapshot();
        return store.Get(snap, HighwayKeyspace.Kv(Name(legacyKvKey)));
    }
}
