using System.Net;
using System.Net.Sockets;

namespace Highway.Server.Internal;

/// <summary>
/// Probe-based helper for finding a free loopback TCP port.
///
/// Strategy (Approach B from design.md): bind a <see cref="TcpListener"/> to
/// port 0, let the OS assign a free port, record it, then stop the listener
/// before returning. Garnet will subsequently bind its own socket to that
/// port. The race window between <c>Stop</c> and Garnet's <c>Bind</c> is
/// extremely small on the loopback interface; we retry up to
/// <see cref="MaxAttempts"/> times on a <see cref="SocketException"/>.
/// </summary>
internal static class EphemeralPort
{
    private const int MaxAttempts = 5;

    /// <summary>
    /// Returns a free loopback port. Throws <see cref="InvalidOperationException"/>
    /// if no port could be acquired after <see cref="MaxAttempts"/> retries.
    /// </summary>
    public static int Probe()
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch (SocketException) when (attempt < MaxAttempts - 1)
            {
                // Port was grabbed between Stop and Garnet's Bind — retry
            }
        }

        throw new InvalidOperationException(
            $"Could not probe a free ephemeral port after {MaxAttempts} attempts.");
    }
}
