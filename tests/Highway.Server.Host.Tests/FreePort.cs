using System.Net;
using System.Net.Sockets;

namespace Highway.Server.Host.Tests;

/// <summary>
/// Probes a free loopback port the way the integration suite does: bind to 0, read
/// the assignment, release. There is an inherent race between release and reuse; it
/// is the accepted practice of the suite and rarely loses.
/// </summary>
internal static class FreePort
{
    public static int Find()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
