using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// Registry, decommission and lease-sweep machinery ported from
/// <c>HighwayCommandBase.Registry</c>, <c>.Decommission</c> and <c>.LeaseSweep</c> onto
/// <see cref="IHighwayStore"/> (039 T7). This is the <b>mirrors-collapse</b> batch (037 §2,
/// T3.3): every Garnet Main-store "mirror" (a newline-delimited string beside an object-store
/// Set) is gone — the set is read directly with <see cref="IHighwayStore.SetMembers"/>.
///
/// <para>The seven collapsed keys — <c>reg:nodes</c>, <c>grplist</c>, <c>job:index</c>,
/// <c>grp:members</c>, <c>node:subs</c>, <c>node:channels</c>, <c>svc:{s}:nodelist</c> — no
/// longer exist as strings: they are the sets they mirrored. Every former mirror reader
/// (<c>ReadMirrorList</c>, <c>SplitList</c>) becomes a <see cref="IHighwayStore.SetMembers"/>
/// call over the same logical name, keyed with the <c>s</c> family. T7's equivalence tests
/// prove the one surviving copy returns the same answer the mirror did.</para>
///
/// <para>The teardown invariants are unchanged (006): removing a node <b>requeues its
/// unacknowledged RPC work</b> (at-least-once survives a node dying) and <b>leaves its
/// subscriber groups untouched</b> unless a PURGE explicitly retires them (pub/sub outlives
/// the process). Every write stages into the caller's batch; the caller commits once.</para>
/// </summary>
internal static class RegistrySupport
{
    /// <summary>Minimum bytes of a well-formed RPC processing entry (i64 + u16 header).</summary>

    // -------------------------------------------------------------------------
    // Membership-set reads (the mirror collapse, T3.3)
    // -------------------------------------------------------------------------

    /// <summary>Reads a membership set as strings. Replaces every Garnet <c>ReadMirrorList</c>.</summary>
    public static string[] ReadSet(IHighwayStore store, IStoreSnapshot snap, string name)
        => [.. store.SetMembers(snap, HighwayKeyspace.SetPrefix(name)).Select(m => Encoding.UTF8.GetString(m))];

    /// <summary>Adds a member to a set (idempotent — the store dedupes on the key).</summary>
    public static void SetAdd(IHighwayStore store, IStoreBatch batch, string name, string member)
        => store.SetAdd(batch, HighwayKeyspace.SetPrefix(name), Encoding.UTF8.GetBytes(member));

    /// <summary>Removes a member from a set.</summary>
    public static void SetRemove(IHighwayStore store, IStoreBatch batch, string name, string member)
        => store.SetRemove(batch, HighwayKeyspace.SetPrefix(name), Encoding.UTF8.GetBytes(member));

    // -------------------------------------------------------------------------
    // Node teardown (Registry)
    // -------------------------------------------------------------------------

    /// <summary>Deletes a node's registration record, its liveness key, and drops it from the registry node set.</summary>
    public static void RemoveRegistration(IHighwayStore store, IStoreBatch batch, string nodeId)
    {
        store.Delete(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(nodeId)));
        store.Delete(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationSeen(nodeId)));
        SetRemove(store, batch, HighwayNames.RegistrationNodeList, nodeId);
    }

    /// <summary>
    /// Stages a liveness beat (feature 060): 8 bytes under <c>reg:seen:{node}</c>. The registration
    /// record — and the catalogue inside it — is not rewritten.
    /// </summary>
    public static void TouchSeen(IHighwayStore store, IStoreBatch batch, string nodeId, long nowTicks)
    {
        var value = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(value, nowTicks);
        store.Set(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationSeen(nodeId)), value);
    }

    /// <summary>
    /// A node's effective last-seen time (feature 060): the newer of the registration record's header
    /// (set when the node registered) and its liveness key (refreshed by every beat). Taking the newer of
    /// the two keeps records written before 060 — header only, no liveness key — reading correctly.
    /// </summary>
    public static long SeenTicks(IHighwayStore store, IStoreSnapshot snap, string nodeId, ReadOnlySpan<byte> record)
    {
        NodeRegistration.Decode(record, out var seen, out _);
        var beat = store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationSeen(nodeId)));
        if (beat is { Length: 8 })
            seen = Math.Max(seen, System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(beat));
        return seen;
    }

    /// <summary>Removes a node from one service's discovery index set. Bounded and self-healing (006).</summary>
    public static void RemoveFromServiceIndex(IHighwayStore store, IStoreBatch batch, string service, string nodeId)
        => SetRemove(store, batch, HighwayNames.RegistrationService(service), nodeId);

    // -------------------------------------------------------------------------
    // Group retirement (Decommission)
    // -------------------------------------------------------------------------

    /// <summary>What a retirement destroyed, so an irreversible act leaves a record (017).</summary>
    internal readonly record struct RetirementOutcome(int Groups, long Messages, long Bytes)
    {
        public static RetirementOutcome operator +(RetirementOutcome a, RetirementOutcome b)
            => new(a.Groups + b.Groups, a.Messages + b.Messages, a.Bytes + b.Bytes);
    }

    /// <summary>
    /// The channels a node subscribes to, from the <c>node:channels</c> set (was a mirror).
    /// Used by BYE PURGE to derive the (channel, group) targets it must retire.
    /// </summary>
    public static string[] ReadNodeChannels(IHighwayStore store, IStoreSnapshot snap, string nodeId)
        => ReadSet(store, snap, HighwayNames.NodeChannels(nodeId));
    /// <summary>
    /// The service names a catalog declares (<c>services[].name</c>). False when the JSON does not parse —
    /// a catalog the server cannot read would leave the node undiscoverable, so it is refused loudly.
    /// </summary>
    public static bool TryParseCatalogServices(ReadOnlySpan<byte> catalog, out string[] services)
    {
        services = [];
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(catalog);
            using var document = System.Text.Json.JsonDocument.ParseValue(ref reader);
            if (!document.RootElement.TryGetProperty("services", out var array)
                || array.ValueKind != System.Text.Json.JsonValueKind.Array)
                return true;   // valid JSON with no services — a pure caller
            var names = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == System.Text.Json.JsonValueKind.Object
                    && entry.TryGetProperty("name", out var name)
                    && name.ValueKind == System.Text.Json.JsonValueKind.String
                    && name.GetString() is { Length: > 0 } value)
                    names.Add(value);
            }
            services = [.. names];
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>The services in the catalog a node registered, or none.</summary>
    public static string[] StoredCatalogServices(IHighwayStore store, IStoreSnapshot snap, string nodeId)
    {
        var record = store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(nodeId)));
        if (record is null || record.Length < NodeRegistration.HeaderSize) return [];
        NodeRegistration.Decode(record, out _, out var catalog);
        return TryParseCatalogServices(catalog, out var services) ? services : [];
    }
}
