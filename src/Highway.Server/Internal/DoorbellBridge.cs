using System.Text;
using Garnet.server;
using Tsavorite.core;

namespace Highway.Server.Internal;

/// <summary>
/// Thin wrapper that publishes doorbell notifications through Garnet's
/// <see cref="SubscribeBroker.PublishNow"/> in a thread-safe manner.
///
/// <para>
/// <c>PublishNow</c> requires <see cref="PinnedSpanByte"/> arguments whose
/// backing memory is pinned for the duration of the call.  This class pins the
/// byte arrays via <c>fixed</c> statements, which is safe because the pinning
/// scope exactly matches the <c>PublishNow</c> call.
/// </para>
///
/// <para>
/// If the broker has not yet been initialised (no subscriber has ever connected)
/// <c>PublishNow</c> safely returns 0 — doorbells are best-effort by contract.
/// </para>
/// </summary>
internal sealed class DoorbellBridge(HighwayGarnetServer server)
{
    /// <summary>
    /// Publishes <paramref name="payload"/> to the Garnet pub/sub channel
    /// <paramref name="channel"/>, waking any subscribed waiters.
    /// </summary>
    /// <param name="channel">Doorbell channel name (UTF-8 string).</param>
    /// <param name="payload">Raw bytes to publish as the message body.</param>
    /// <returns>Number of subscribers notified, or 0 if none.</returns>
    public unsafe int Ring(string channel, ReadOnlySpan<byte> payload)
    {
        var broker = server.SubscribeBroker;
        if (broker is null)
            return 0;

        // Encode the channel name to UTF-8.
        byte[] ch = Encoding.UTF8.GetBytes(channel);
        // Copy payload to a managed array so we can pin it.
        byte[] body = payload.ToArray();

        fixed (byte* c = ch)
        fixed (byte* b = body)
        {
            return broker.PublishNow(
                PinnedSpanByte.FromPinnedPointer(c, ch.Length),
                PinnedSpanByte.FromPinnedPointer(b, body.Length));
        }
    }

    /// <summary>
    /// Convenience overload that encodes a UTF-8 string payload before publishing.
    /// </summary>
    public int Ring(string channel, string payload)
        => Ring(channel, Encoding.UTF8.GetBytes(payload));

    /// <summary>
    /// Convenience overload for publishing a single byte array.
    /// </summary>
    public int Ring(string channel, byte[] payload)
        => Ring(channel, payload.AsSpan());
}
