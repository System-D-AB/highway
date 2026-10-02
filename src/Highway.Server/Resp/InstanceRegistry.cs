using Highway.Abstractions.Observability;
using Highway.Server.Observability;

namespace Highway.Server.Resp;

/// <summary>
/// The live view of <b>which processes stand behind each node name</b> (feature 072; grew out of 048's
/// node → observed-address map). A Highway client names every connection
/// <c>CLIENT SETNAME {node}@{instance}</c>, where the instance id is random per process; the broker splits
/// on the last <c>@</c> (a node name can never contain one) and counts the distinct instances among a
/// node's live connections. When more than one process uses a name the broker says so — shared names
/// share one heartbeat (a dead copy is not noticed) and one set of claims (a copy that departs returns its
/// siblings' work).
///
/// <para>It is an <b>observation, never a declaration</b> (constraints C7.3): nothing here is stored or
/// replicated, and nothing here decides anything. The address is where the broker sees the connection from;
/// the metadata (host, pid, hosting name, start time, client version) is what <c>HW.SESSION</c> said, kept
/// for display. After a failover clients reconnect and re-declare, and the new primary rebuilds the view.</para>
///
/// <para>All mutation happens on connection naming and teardown and on <c>HW.SESSION</c> — rare events —
/// under one lock. Nothing on the command path touches it.</para>
/// </summary>
internal sealed class InstanceRegistry
{
    /// <summary>The instance key for connections named without an instance id (another client, an older build).</summary>
    public const string Unidentified = "unidentified";

    /// <summary>At most this many (node, instance) metadata entries are kept; the oldest disconnected go first.</summary>
    public const int MaxDescribed = 4096;

    /// <summary>
    /// Metadata outlives the instance's last connection by this long, so a reconnect does not blank the
    /// view (a session is not re-sent on a plain reconnect). One hour is when the dashboard calls a node absent.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private const int InstanceIdLength = 12;
    private const int MaxFieldLength = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Node, string Instance), Described> _described = [];
    private readonly Dictionary<string, int> _liveCount = new(StringComparer.Ordinal);
    private readonly FlightRecorder? _recorder;
    private readonly Func<long> _clock;

    private sealed record Connection(string Node, string Instance, string Endpoint);

    private sealed class Described
    {
        public InstanceMeta? Meta;
        public long LastLiveTicks;
    }

    /// <param name="recorder">Receives <see cref="HighwayEventType.NodeNameShared"/> on each transition; null in tests.</param>
    /// <param name="clock">UTC ticks; injectable for the retention tests.</param>
    public InstanceRegistry(FlightRecorder? recorder = null, Func<long>? clock = null)
    {
        _recorder = recorder;
        _clock = clock ?? (() => DateTime.UtcNow.Ticks);
    }

    /// <summary>Records (or refreshes) a named connection. <paramref name="clientName"/> is the raw SETNAME value.</summary>
    public void Record(string connectionId, string clientName, string endpoint)
    {
        if (string.IsNullOrEmpty(connectionId) || string.IsNullOrEmpty(clientName) || string.IsNullOrEmpty(endpoint))
            return;
        var (node, instance) = Parse(clientName);
        if (node.Length == 0) return;

        // Transitions are raised under the lock (072 review, finding 6): raised after it, a "shared" and a
        // "back to one" computed on two threads could reach the log in the wrong order. They are rare, and the
        // recorder never blocks.
        lock (_gate)
        {
            Sweep();   // before Touch: an instance back after the retention must not revive its old details
            if (_byConnection.TryGetValue(connectionId, out var previous) && previous.Node != node)
            {
                _byConnection.Remove(connectionId);   // a connection renamed to another node leaves the first
                Touch(previous.Node, previous.Instance);
                Raise(Evaluate(previous.Node));
            }
            _byConnection[connectionId] = new Connection(node, instance, endpoint);
            Touch(node, instance);
            Raise(Evaluate(node));
        }
    }

    /// <summary>Drops a connection on teardown.</summary>
    public void Remove(string connectionId)
    {
        lock (_gate)
        {
            if (_byConnection.Remove(connectionId, out var gone))
            {
                Touch(gone.Node, gone.Instance);
                Raise(Evaluate(gone.Node));
            }
        }
    }

    /// <summary>
    /// What <c>HW.SESSION</c> said about the process (072). Display only: a malformed or missing description
    /// is simply absent, and an over-full registry drops the newest disconnected description rather than
    /// a live one.
    /// </summary>
    public void Describe(string node, InstanceMeta meta)
    {
        if (!IsInstanceId(meta.Id)) return;
        var key = (Sanitise(node), meta.Id);
        lock (_gate)
        {
            Sweep();
            if (!_described.TryGetValue(key, out var entry))
            {
                if (_described.Count >= MaxDescribed && !EvictOldestDisconnected()) return;
                entry = new Described { LastLiveTicks = _clock() };
                _described[key] = entry;
            }
            entry.Meta = meta with
            {
                Host = Clip(meta.Host), Hosting = Clip(meta.Hosting), Client = Clip(meta.Client),
            };
        }
    }

    /// <summary>The observed address of one of <paramref name="node"/>'s live connections, or null (048).</summary>
    public string? AddressOf(string node)
    {
        var wanted = Sanitise(node);
        lock (_gate)
        {
            foreach (var c in _byConnection.Values)
                if (string.Equals(c.Node, wanted, StringComparison.Ordinal))
                    return c.Endpoint;
        }
        return null;
    }

    /// <summary>The live processes behind <paramref name="node"/>, each with what its session said about it.</summary>
    public IReadOnlyList<InstanceView> InstancesOf(string node)
    {
        var wanted = Sanitise(node);
        lock (_gate)
        {
            Sweep();
            var views = new Dictionary<string, InstanceView>(StringComparer.Ordinal);
            foreach (var c in _byConnection.Values)
            {
                if (!string.Equals(c.Node, wanted, StringComparison.Ordinal) || views.ContainsKey(c.Instance)) continue;
                _described.TryGetValue((wanted, c.Instance), out var d);
                var m = d?.Meta;
                views[c.Instance] = new InstanceView(c.Instance, c.Endpoint, m?.Host, m?.Pid, m?.Hosting, m?.Started, m?.Client);
            }
            return [.. views.Values.OrderBy(v => v.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// The connection name a Highway client sends for a node — <b>the client's rule, mirrored</b>
    /// (<c>HighwayConnectionConfiguration.Sanitise</c>): every character outside <c>[A-Za-z0-9-_.]</c>
    /// becomes '_'. Matching on this form on both sides is what lets a node named with a space, a '$' (a
    /// Windows service such as <c>MSSQL$X</c>) or a non-ASCII machine name show as connected. Two names that
    /// differ only in such characters share a form; that only merges their display.
    /// </summary>
    public static string Sanitise(string name)
        => string.Create(name.Length, name, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
                span[i] = s[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' ? s[i] : '_';
        });

    /// <summary>Splits <c>{node}@{instance}</c> on the last '@'; a suffix that is not an instance id is part of the name.</summary>
    internal static (string Node, string Instance) Parse(string clientName)
    {
        var at = clientName.LastIndexOf('@');
        if (at > 0 && IsInstanceId(clientName.AsSpan(at + 1)))
            return (clientName[..at], clientName[(at + 1)..]);
        return (clientName, Unidentified);
    }

    private static bool IsInstanceId(ReadOnlySpan<char> s)
    {
        if (s.Length != InstanceIdLength) return false;
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    private static string? Clip(string? s) => s is { Length: > MaxFieldLength } ? s[..MaxFieldLength] : s;

    // ---- under _gate --------------------------------------------------------------------------

    private void Touch(string node, string instance)
    {
        if (_described.TryGetValue((node, instance), out var d)) d.LastLiveTicks = _clock();
    }

    private HashSet<(string Node, string Instance)> LiveKeys()
        => [.. _byConnection.Values.Select(c => (c.Node, c.Instance))];

    private void Sweep()
    {
        var cutoff = _clock() - Retention.Ticks;
        HashSet<(string, string)>? live = null;
        List<(string, string)>? expired = null;
        foreach (var (key, d) in _described)
            if (d.LastLiveTicks < cutoff && !(live ??= LiveKeys()).Contains(key))
                (expired ??= []).Add(key);
        if (expired is not null)
            foreach (var key in expired) _described.Remove(key);
    }

    private bool EvictOldestDisconnected()
    {
        (string, string)? oldest = null;
        var oldestTicks = long.MaxValue;
        var live = LiveKeys();
        foreach (var (key, d) in _described)
        {
            if (d.LastLiveTicks >= oldestTicks || live.Contains(key)) continue;
            oldest = key;
            oldestTicks = d.LastLiveTicks;
        }
        if (oldest is not { } victim) return false;
        _described.Remove(victim);
        return true;
    }

    private sealed record Transition(string Node, int Count, string Detail);

    /// <summary>Recounts one node's live instances; a 1→N or N→1 change is returned for <see cref="Raise"/>.</summary>
    private Transition? Evaluate(string node)
    {
        var instances = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in _byConnection.Values)
            if (c.Node == node) instances.TryAdd(c.Instance, c.Endpoint);

        var count = instances.Count;
        var before = _liveCount.GetValueOrDefault(node);
        if (count == 0) _liveCount.Remove(node);
        else _liveCount[node] = count;

        var shared = count > 1;
        if (shared == before > 1) return null;
        if (!shared && count == 0) return null;   // a shared node whose every connection closed at once: nothing to say

        var detail = string.Join(", ", instances.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i =>
        {
            var m = _described.TryGetValue((node, i.Key), out var d) ? d.Meta : null;
            return m is null ? $"{i.Key}@{i.Value}" : $"{i.Key}@{i.Value} ({m.Host} pid {m.Pid}{(m.Hosting is null ? "" : " " + m.Hosting)})";
        }));
        return new Transition(node, count, detail);
    }

    private void Raise(Transition? t)
    {
        if (t is null || _recorder is null) return;
        try { _recorder.Record(HighwayEventType.NodeNameShared, t.Node, nodeId: t.Node, errorCode: t.Detail, count: t.Count); }
        catch (Exception) { /* display and diagnostics only: never affects CLIENT SETNAME's +OK */ }
    }
}

/// <summary>What a process said about itself in <c>HW.SESSION</c> (072). Every field is display-only.</summary>
internal sealed record InstanceMeta(string Id, string? Host, int? Pid, string? Hosting, DateTimeOffset? Started, string? Client);

/// <summary>One live process behind a node name, for the dashboard (072).</summary>
internal sealed record InstanceView(
    string Id, string SeenFrom, string? Host, int? Pid, string? Hosting, DateTimeOffset? Started, string? Client);
