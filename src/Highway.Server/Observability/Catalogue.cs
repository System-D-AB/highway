using System.Text.Json;
using Highway.Server.Internal;

namespace Highway.Server.Observability;

/// <summary>What an entity in the catalogue actually is (022 R1.1).</summary>
internal enum EntityKind
{
    /// <summary>An RPC service — <c>ExecuteAsync</c> / <c>AsyncService&lt;,&gt;</c>.</summary>
    Service,

    /// <summary>A queue — <c>SendAsync</c> / <c>IProcess&lt;T&gt;</c> (014).</summary>
    Queue,

    /// <summary>A pub/sub channel — <c>PublishAsync</c> / <c>ISubscribe&lt;T&gt;</c>.</summary>
    Channel,

    /// <summary>One subscriber group of a channel. Since 018 it <b>is</b> a queue.</summary>
    Group,

    /// <summary>A node id. Appears as a recorder name because <c>HW.HEARTBEAT</c> records under it.</summary>
    Node,

    /// <summary>Broker-internal, never a user concept — <c>hw.replies</c> (015).</summary>
    Internal,

    /// <summary>
    /// Could not be classified. <b>A real member, deliberately</b> (R1.4): a wrong classification
    /// is worse than an admitted gap, because an operator stops questioning it.
    /// </summary>
    Unknown,
}

/// <summary>
/// Whether an entity is actually being served (022 review R-2A).
///
/// <para>Replaces a <c>HasLiveHost</c> boolean, which could not distinguish "declared by a node
/// that has gone quiet" from "never declared by anyone" — and those need different actions.</para>
/// </summary>
internal enum EntityState
{
    /// <summary>Declared by at least one node that is currently within <c>NodeExpiry</c>.</summary>
    Live,

    /// <summary>Declared, but every node that declared it has gone stale.</summary>
    HostStale,

    /// <summary>
    /// Seen in traffic but declared by nobody — a service being called that was never deployed,
    /// or a queue being sent to with no processor. Invisible in the old dashboard, and one of
    /// the failures most worth surfacing.
    /// </summary>
    NeverDeclared,

    /// <summary>Provenance could not be established — for example when the registry is unreadable.</summary>
    Unknown,
}

/// <summary>One row of the catalogue, classified by the server.</summary>
internal sealed record CatalogueEntryDto(
    string Name,
    EntityKind Kind,
    EntityState State,
    string? ParentChannel,
    IReadOnlyList<string> Hosts);

/// <summary>One node, with what it declared and how long since it said so.</summary>
internal sealed record NodeDto(
    string Name,
    TimeSpan SinceLastSeen,
    bool IsLive,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Queues,
    IReadOnlyList<string> Channels);

/// <summary>
/// Classifies broker entities, and assembles the catalogue from the two sources that know about
/// them (022 review R-1A).
///
/// <para><b>Declared</b> — the node registry. Every heartbeat since feature 006 has carried each
/// node's services, channels and queues, and nothing has ever read it back for display.</para>
///
/// <para><b>Observed</b> — the recorder's name index. In-process, needs no connection, and it is
/// the only thing that knows about an entity which traffic addressed but no node ever declared.
/// That case — a service being called that was never deployed — is precisely the failure the old
/// dashboard could not distinguish from a healthy one.</para>
///
/// <para>The union is computed at read time. <b>Neither source is new storage.</b></para>
/// </summary>
internal static class Catalogue
{
    /// <summary>
    /// Classification lives here, on the server, and nowhere else (022 decision 2).
    ///
    /// <para><c>@</c> separates a derived group-queue name <i>because the server derives it that
    /// way</i> (018 T0 reserves the character for exactly this reason). That is a server fact,
    /// and a <c>name.includes('@')</c> in the browser would be a second implementation of a
    /// naming convention — the drift this project keeps finding.</para>
    /// </summary>
    public static (EntityKind Kind, string? ParentChannel) Classify(
        string name,
        IReadOnlySet<string> declaredServices,
        IReadOnlySet<string> declaredQueues,
        IReadOnlySet<string> declaredChannels,
        IReadOnlySet<string> nodeNames)
    {
        if (name == Commands.HwReplyCommand.RecorderName)
            return (EntityKind.Internal, null);

        var at = name.IndexOf('@');
        if (at > 0 && at < name.Length - 1)
            return (EntityKind.Group, name[..at]);

        if (declaredServices.Contains(name)) return (EntityKind.Service, null);
        if (declaredQueues.Contains(name)) return (EntityKind.Queue, null);
        if (declaredChannels.Contains(name)) return (EntityKind.Channel, null);
        if (nodeNames.Contains(name)) return (EntityKind.Node, null);

        return (EntityKind.Unknown, null);
    }

    /// <summary>
    /// Reads one node's declaration. The catalog is the JSON the client sent with its heartbeat;
    /// a record that cannot be parsed yields empty lists rather than being dropped, because a
    /// node with an unreadable catalog is a misconfiguration worth seeing (022 error handling).
    /// </summary>
    public static NodeDto ReadNode(string nodeId, ReadOnlySpan<byte> record, TimeSpan expiry)
    {
        if (record.Length < NodeRegistration.HeaderSize)
            return new NodeDto(nodeId, TimeSpan.MaxValue, false, [], [], []);

        NodeRegistration.Decode(record, out var seenTicks, out var catalog);

        var since = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - seenTicks));
        var services = new List<string>();
        var queues = new List<string>();
        var channels = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(catalog.ToArray());
            ReadNames(doc.RootElement, "services", services);
            ReadNames(doc.RootElement, "queues", queues);
            ReadNames(doc.RootElement, "channels", channels);
        }
        catch (JsonException)
        {
            // Left empty on purpose: the node still appears, showing that it declared nothing
            // readable. Omitting it would hide the misconfiguration entirely.
        }

        return new NodeDto(nodeId, since, since <= expiry, services, queues, channels);
    }

    /// <summary>
    /// Reads a catalog array. Entries are objects with a <c>name</c>, but a bare string is
    /// accepted too — the shape that looked obviously correct when writing 017's tests, and was
    /// silently parsed as "no services at all".
    /// </summary>
    private static void ReadNames(JsonElement root, string property, List<string> into)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } bare)
                into.Add(bare);
            else if (entry.ValueKind == JsonValueKind.Object
                     && entry.TryGetProperty("name", out var name)
                     && name.ValueKind == JsonValueKind.String
                     && name.GetString() is { Length: > 0 } value)
                into.Add(value);
        }
    }
}
