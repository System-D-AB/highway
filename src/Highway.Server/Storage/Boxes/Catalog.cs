using System.Text.Json;

namespace Highway.Server.Storage.Boxes;

/// <summary>
/// The system box's catalog (065): name → box id, id → metadata, the id allocator and the catalog
/// version. Every service, queue and group queue lives in its own box; the catalog is how a name finds it
/// and how everything that exists is listed without scanning data.
///
/// <para><b>Allocation is committed on its own, under a catalog lock.</b> Two commands creating different
/// names concurrently must not read the same allocator value, and a command's own batch is not committed
/// until the command ends — so <see cref="Ensure"/> creates the box in a separate batch before the caller
/// takes the box lock. Callers never hold a box lock while calling it (the stripes are not re-entrant).
/// Ids are never reused: a retired box's id stays burnt, so a stale token or a late write to it is
/// detectable rather than landing in someone else's queue.</para>
/// </summary>
internal static class Catalog
{
    internal const string LockName = "\u0000catalog";

    internal sealed record BoxMeta(int V, string Name, BoxKeys.Kind Kind, long Created, string State, long? RetiredAt);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The box for (kind, name), or null when it does not exist. No writes.</summary>
    public static uint? Find(IHighwayStore store, IStoreBatch batch, BoxRuntime rt, BoxKeys.Kind kind, string name)
    {
        if (rt.TryGetId(kind, name, out var cached)) return cached;
        var raw = store.Read(batch, BoxKeys.NameIndexKey(kind, name));
        if (raw is not { Length: 4 }) return null;
        var box = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw);
        rt.Remember(kind, name, box);
        return box;
    }

    /// <summary>The box for (kind, name), creating and committing it if it does not exist yet.</summary>
    public static uint Ensure(IHighwayStore store, BoxRuntime rt, StripedLock locks, BoxKeys.Kind kind, string name, long nowTicks)
    {
        if (rt.TryGetId(kind, name, out var cached)) return cached;

        using (var probe = store.NewBatch())
            if (Find(store, probe, rt, kind, name) is { } existing) return existing;

        using var _ = locks.Lock(LockName);
        using var batch = store.NewBatch();
        if (Find(store, batch, rt, kind, name) is { } raced) return raced;   // created while we waited

        var allocatorKey = BoxKeys.SystemKey(BoxKeys.Allocator);
        var next = (uint)Math.Max(BoxKeys.ReadI64(store.Read(batch, allocatorKey)), BoxKeys.FirstWorkBox);
        store.Set(batch, allocatorKey, BoxKeys.I64(next + 1L));
        store.Set(batch, BoxKeys.NameIndexKey(kind, name), BoxKeys.Box(next));
        store.Set(batch, BoxKeys.MetaKey(next),
            JsonSerializer.SerializeToUtf8Bytes(new BoxMeta(1, name, kind, nowTicks, "active", null), Json));
        BumpVersion(store, batch);
        batch.Commit();

        rt.Remember(kind, name, next);
        return next;
    }

    /// <summary>
    /// Retires (kind, name): the name is released, the box's whole range is deleted in one range delete,
    /// and its metadata is kept as <c>retired</c> (the id stays burnt). Returns the retired box, or null
    /// when the name did not exist. The caller commits, then calls <see cref="BoxRuntime.Forget"/>.
    /// </summary>
    public static uint? Retire(IHighwayStore store, IStoreBatch batch, BoxRuntime rt, BoxKeys.Kind kind, string name, long nowTicks)
    {
        if (Find(store, batch, rt, kind, name) is not { } box) return null;
        store.Delete(batch, BoxKeys.NameIndexKey(kind, name));
        store.DeleteRange(batch, BoxKeys.Box(box));
        var meta = ReadMeta(store, batch, box) ?? new BoxMeta(1, name, kind, nowTicks, "active", null);
        store.Set(batch, BoxKeys.MetaKey(box),
            JsonSerializer.SerializeToUtf8Bytes(meta with { State = "retired", RetiredAt = nowTicks }, Json));
        BumpVersion(store, batch);
        return box;
    }

    public static BoxMeta? ReadMeta(IHighwayStore store, IStoreBatch batch, uint box)
    {
        var raw = store.Read(batch, BoxKeys.MetaKey(box));
        return raw is null ? null : JsonSerializer.Deserialize<BoxMeta>(raw, Json);
    }

    /// <summary>Every live (kind, name, box), in name-index order — no data is scanned.</summary>
    public static List<(BoxKeys.Kind Kind, string Name, uint Box)> List(IHighwayStore store, IStoreBatch batch, BoxKeys.Kind? only = null)
    {
        var prefix = only is { } k
            ? new byte[] { 0, 0, 0, 0, BoxKeys.NameIndex, (byte)k }
            : [0, 0, 0, 0, BoxKeys.NameIndex];
        var result = new List<(BoxKeys.Kind, string, uint)>();
        var from = prefix;
        while (true)
        {
            var page = store.Scan(batch, prefix, from, 256);
            foreach (var (key, value) in page)
            {
                var kind = (BoxKeys.Kind)key[5];
                var name = Layout.KeyEncoding.ReadString(key.AsSpan(6), out _);
                result.Add((kind, name, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value)));
            }
            if (page.Count < 256) return result;
            from = [.. page[^1].Key, 0x00];   // just past the last key
        }
    }

    public static long Version(IHighwayStore store, IStoreBatch batch)
        => BoxKeys.ReadI64(store.Read(batch, BoxKeys.SystemKey(BoxKeys.CatalogVersion)));

    private static void BumpVersion(IHighwayStore store, IStoreBatch batch)
    {
        var key = BoxKeys.SystemKey(BoxKeys.CatalogVersion);
        store.Set(batch, key, BoxKeys.I64(BoxKeys.ReadI64(store.Read(batch, key)) + 1));
    }
}
