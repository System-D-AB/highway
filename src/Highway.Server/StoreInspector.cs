using System.Text;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;

namespace Highway.Server;

/// <summary>
/// Test/operator read access to broker state by the legacy key names the suite has always used
/// (<c>hw:q:{queue}:q</c>, <c>hw:svc:{service}:dlq</c>, <c>hw:q:{queue}:proc:{node}</c>, …) — mapped onto
/// the box layout (065): a work structure's name resolves to its box and the suffix to a sub-space or a
/// counter. Registry and membership names (sets, KV) read the system box's family keys directly.
/// Read-only; nothing here is on a hot path.
/// </summary>
internal sealed class StoreInspector(IHighwayStore store)
{
    private static string Name(string legacyKey)
        => legacyKey.StartsWith("hw:", StringComparison.Ordinal) ? legacyKey[3..] : legacyKey;

    private enum Part { Ready, Dead, Delayed, Bytes, Workers, InFlight }

    /// <summary>Parses <c>q:{name}:{part}</c> / <c>svc:{name}:{part}</c> (part: q, dlq, delayed, bytes, nodes, proc:{node}).</summary>
    private static bool TryWork(string legacyKey, out BoxKeys.Kind kind, out string name, out Part part, out string? node)
    {
        var key = Name(legacyKey);
        kind = BoxKeys.Kind.Queue;
        name = "";
        part = Part.Ready;
        node = null;
        string rest;
        if (key.StartsWith("q:", StringComparison.Ordinal)) rest = key[2..];
        else if (key.StartsWith("svc:", StringComparison.Ordinal)) { kind = BoxKeys.Kind.Service; rest = key[4..]; }
        else return false;

        var proc = rest.LastIndexOf(":proc:", StringComparison.Ordinal);
        if (proc > 0)
        {
            name = rest[..proc];
            node = rest[(proc + ":proc:".Length)..];
            part = Part.InFlight;
            return true;
        }
        foreach (var (suffix, p) in new[] { (":q", Part.Ready), (":dlq", Part.Dead), (":delayed", Part.Delayed), (":bytes", Part.Bytes), (":nodes", Part.Workers) })
        {
            if (!rest.EndsWith(suffix, StringComparison.Ordinal)) continue;
            name = rest[..^suffix.Length];
            part = p;
            return name.Length > 0;
        }
        return false;
    }

    private T WithBox<T>(BoxKeys.Kind kind, string name, T whenAbsent, Func<WorkBox, T> read)
    {
        var rt = new BoxRuntime();
        using var batch = store.NewBatch();
        if (Catalog.Find(store, batch, rt, kind, name) is not { } box) return whenAbsent;
        using var wb = new WorkBox(store, rt, box, tracksBytes: kind == BoxKeys.Kind.Queue, batch,
            rawEntries: kind == BoxKeys.Kind.DeadFeed);
        return read(wb);
    }

    /// <summary>Entries in a legacy list: ready queue, dead letters, or a node's claims.</summary>
    public long ListLength(string legacyListKey)
    {
        if (TryWork(legacyListKey, out var kind, out var name, out var part, out var node))
            return WithBox(kind, name, 0L, wb => part switch
            {
                Part.Ready => wb.Ready,
                Part.Dead => wb.Dead,
                Part.Delayed => wb.Delayed,
                Part.InFlight => wb.InFlightFor(node!).Count,
                _ => 0L,
            });
        using var snap = store.Snapshot();
        return store.ListLength(snap, HighwayKeyspace.ListPrefix(Name(legacyListKey)));
    }

    /// <summary>
    /// The raw entries of a legacy list: ready → queue entries; dead → dead-letter records; a node's claims →
    /// the claimed queue entries (the in-flight wrapper removed).
    /// </summary>
    public IReadOnlyList<byte[]> ListEntries(string legacyListKey)
    {
        if (TryWork(legacyListKey, out var kind, out var name, out var part, out var node))
            return WithBox<IReadOnlyList<byte[]>>(kind, name, [], wb => part switch
            {
                Part.Ready => wb.ReadyEntries(),
                Part.Dead => [.. wb.PeekDead(4096).Select(e => e.Value)],
                Part.InFlight => [.. wb.InFlightFor(node!).Select(r => r.Entry)],
                _ => [],
            });
        using var batch = store.NewBatch();
        return store.ListDrain(batch, HighwayKeyspace.ListPrefix(Name(legacyListKey)));   // uncommitted: read only
    }

    public long Counter(string legacyCounterKey)
    {
        if (TryWork(legacyCounterKey, out var kind, out var name, out var part, out _) && part == Part.Bytes)
            return WithBox(kind, name, 0L, wb => wb.Bytes);
        using var batch = store.NewBatch();
        return store.Increment(batch, HighwayKeyspace.Counter(Name(legacyCounterKey)), 0);   // zero-delta read, uncommitted
    }

    public IReadOnlyList<string> SetMembers(string legacySetKey)
    {
        if (TryWork(legacySetKey, out var kind, out var name, out var part, out _) && part == Part.Workers)
            return WithBox<IReadOnlyList<string>>(kind, name, [], wb => wb.Workers());
        using var snap = store.Snapshot();
        return [.. store.SetMembers(snap, HighwayKeyspace.SetPrefix(Name(legacySetKey))).Select(m => Encoding.UTF8.GetString(m))];
    }

    public long SortedSetCount(string legacyZsetKey)
    {
        if (TryWork(legacyZsetKey, out var kind, out var name, out var part, out _) && part == Part.Delayed)
            return WithBox(kind, name, 0L, wb => wb.Delayed);
        using var snap = store.Snapshot();
        return store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(Name(legacyZsetKey)));
    }

    public bool ReplySlotExists(string requestId, long? nowTicks = null)
    {
        using var snap = store.Snapshot();
        return store.GetLive(snap, HighwayKeyspace.Kv(HighwayNames.ReplySlot(requestId)), nowTicks ?? DateTime.UtcNow.Ticks) is not null;
    }

    public byte[]? KvGet(string legacyKvKey)
    {
        using var snap = store.Snapshot();
        return store.Get(snap, HighwayKeyspace.Kv(Name(legacyKvKey)));
    }
}
