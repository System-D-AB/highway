using System.Text;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Resp;

/// <summary>
/// Read-only introspection over an <see cref="IHighwayStore"/> for the embedded test server's
/// diagnostic hooks (040 T8). Under Garnet, <c>BrokerState</c> read broker state by opening a
/// self-connection and issuing raw Redis <c>SCAN</c>/<c>GET</c>/<c>LLEN</c>/<c>ZCARD</c> — commands
/// the RESP server deliberately does not serve. In-process, the store <b>is</b> right here, so the
/// hooks read it directly: enumerate the whole keyspace, decode the family tag and logical name,
/// and answer the same questions.
///
/// <para>This exists only for the test server's <c>ReadQueueStateAsync</c>/<c>ReadCatalogueAsync</c>/
/// <c>ReadNodesAsync</c> — the production dashboard's own read path is out of scope for 040 and is
/// handled when the dashboard moves off the Garnet self-connection.</para>
/// </summary>
internal static class StoreInspection
{
    private const byte TagList = (byte)'q';
    private const byte TagKv = (byte)'k';
    private const byte TagSet = (byte)'s';

    /// <summary>The whole committed keyspace, from whichever concrete store backs the server.</summary>
    public static List<(byte[] Key, byte[] Value)> Dump(IHighwayStore store) => store switch
    {
        InMemoryStore m => m.DumpData(),
        Storage.Rocks.RocksDbStore r => r.DumpData(),
        _ => throw new NotSupportedException($"store type {store.GetType().Name} is not inspectable"),
    };

    /// <summary>
    /// Decodes the logical <c>&lt;name&gt;</c> from a key, given its expected family tag. Returns null
    /// when the key is a different family. Mirrors <see cref="KeyEncoding.WriteString"/>: the name is
    /// escaped UTF-8 terminated by <c>0x00 0x00</c>; a list/zset/set key then carries a suffix which
    /// is ignored here (we only want the name).
    /// </summary>
    public static string? DecodeName(byte[] key, byte familyTag)
    {
        if (key.Length < 1 || key[0] != familyTag) return null;

        var bytes = new List<byte>(key.Length - 1);
        var i = 1;
        while (i < key.Length)
        {
            var b = key[i];
            if (b == 0x00)
            {
                if (i + 1 >= key.Length) return null;      // truncated
                var next = key[i + 1];
                if (next == 0x00) break;                    // terminator 0x00 0x00 — name complete
                if (next == 0xFF) { bytes.Add(0x00); i += 2; continue; } // escaped NUL
                return null;                                // malformed escape
            }
            bytes.Add(b);
            i++;
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>Every distinct queue name that exists as a live queue list (<c>q:{queue}:q</c>).</summary>
    public static IReadOnlyList<string> QueueNames(IHighwayStore store)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (key, _) in Dump(store))
        {
            var decoded = DecodeName(key, TagList);
            // The list family also holds proc/dlq lists; only the live queue matches "q:{q}:q".
            if (decoded is { } n && n.StartsWith("q:", StringComparison.Ordinal) && n.EndsWith(":q", StringComparison.Ordinal))
            {
                var queue = n["q:".Length..^":q".Length];
                if (queue.Length > 0) names.Add(queue);
            }
        }
        return [.. names];
    }

    /// <summary>Reads a registration record by node id, or null when absent.</summary>
    public static byte[]? RegistrationRecord(IHighwayStore store, IStoreSnapshot snap, string nodeId)
        => store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(nodeId)));

    /// <summary>The registered node ids, from the registration-node set (was the <c>reg:nodes</c> mirror).</summary>
    public static IReadOnlyList<string> NodeIds(IHighwayStore store, IStoreSnapshot snap)
        => [.. store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.RegistrationNodeList))
                    .Select(m => Encoding.UTF8.GetString(m))];

    /// <summary>The members backing a derived group (<c>{channel}@{group}</c>), was the <c>grp:members</c> mirror.</summary>
    public static string[] GroupMembers(IHighwayStore store, IStoreSnapshot snap, string channel, string group)
        => [.. store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.GroupMembers(channel, group)))
                    .Select(m => Encoding.UTF8.GetString(m))];

    /// <summary>Queues that have at least one recurring schedule, from the <c>job:index</c> set (028).</summary>
    public static IReadOnlyList<string> NamesWithJobSchedules(IHighwayStore store, IStoreSnapshot snap)
        => [.. store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.JobIndex))
                    .Select(m => Encoding.UTF8.GetString(m))];
}
