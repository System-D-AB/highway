using System.Collections.Concurrent;

namespace Highway.Server.Resp;

/// <summary>
/// Feature 048 — the live map of <b>node name → observed peer address</b>, populated from the
/// <c>CLIENT SETNAME</c> a Highway client sends on connect and cleared when the connection ends.
/// It restores the dashboard's "Seen from" column, which the RESP rewrite (040/041) left
/// non-functional: the in-process broker no longer joined the node registry against
/// <c>CLIENT LIST</c>, so every node showed "not connected".
///
/// <para>It is an <b>observation, never a declaration</b> (constraints C7.3): the address is where
/// the broker currently sees the connection coming from, which behind NAT or a load balancer is
/// true but not necessarily dialable. A node with no live named connection has no entry, and the
/// dashboard honestly shows "not connected".</para>
/// </summary>
internal sealed class ObservedAddressRegistry
{
    // connectionId → (node name, peer endpoint). Keyed by connection so teardown is exact even when
    // a node holds several connections; the read scans values (node counts are small, reads rare).
    private readonly ConcurrentDictionary<string, (string Node, string Endpoint)> _byConnection
        = new(StringComparer.Ordinal);

    /// <summary>Records (or refreshes) the observed address for a named connection.</summary>
    public void Record(string connectionId, string node, string endpoint)
    {
        if (string.IsNullOrEmpty(connectionId) || string.IsNullOrEmpty(node) || string.IsNullOrEmpty(endpoint))
            return;
        _byConnection[connectionId] = (node, endpoint);
    }

    /// <summary>Drops a connection's entry on teardown.</summary>
    public void Remove(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    /// <summary>The observed address for <paramref name="node"/>, or null if it has no live named connection now.</summary>
    public string? AddressOf(string node)
    {
        foreach (var (_, v) in _byConnection)
            if (string.Equals(v.Node, node, StringComparison.Ordinal))
                return v.Endpoint;
        return null;
    }
}
