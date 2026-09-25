using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace Highway.Client.Engine;

/// <summary>
/// Whether two <c>host:port</c> spellings name the same broker (feature 069, L2).
///
/// <para>The failover walk compares the active host with roster and bootstrap entries. Compared as
/// strings, <c>localhost:6500</c> and the roster's advertised <c>127.0.0.1:6500</c> differ — so the dead
/// master was neither excluded nor tried last, and as priority 1 it was probed first, costing a full
/// connect timeout on every failover. That happens in production whenever the configured spelling differs
/// from the advertised one. Two endpoints are the same when their ports match and their hosts are equal
/// ignoring case, are both loopback, or resolve to a common address (resolution cached; a name that does
/// not resolve compares by spelling only).</para>
/// </summary>
internal static class EndpointIdentity
{
    private static readonly ConcurrentDictionary<string, IPAddress[]> Resolved = new(StringComparer.OrdinalIgnoreCase);

    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (!TrySplit(a, out var hostA, out var portA) || !TrySplit(b, out var hostB, out var portB)) return false;
        if (portA != portB) return false;
        if (string.Equals(hostA, hostB, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsLoopback(hostA) && IsLoopback(hostB)) return true;
        var addressesA = Resolve(hostA);
        return addressesA.Length > 0 && Resolve(hostB).Any(x => addressesA.Contains(x));
    }

    private static bool TrySplit(string endpoint, out string host, out int port)
    {
        host = endpoint;
        port = 6379;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || endpoint.EndsWith(']')) { host = endpoint.Trim('[', ']'); return true; }
        host = endpoint[..colon].Trim('[', ']');
        return int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    private static bool IsLoopback(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static IPAddress[] Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        return Resolved.GetOrAdd(host, static h =>
        {
            try { return Dns.GetHostAddresses(h); }
            catch (Exception) { return []; }
        });
    }
}
