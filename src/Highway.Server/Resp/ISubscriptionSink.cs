namespace Highway.Server.Resp;

/// <summary>
/// The per-connection view of the doorbell subscription registry (040 T7). A subscribed
/// connection registers each channel here; the registry fans a server-internal doorbell publish
/// out to every connection subscribed to that channel. Kept as a seam so the session's
/// subscribe/unsubscribe bookkeeping is testable without a real registry or socket.
/// </summary>
internal interface ISubscriptionSink
{
    /// <summary>Registers this connection for <paramref name="channel"/>. Returns the new total subscription count.</summary>
    int Subscribe(string channel);

    /// <summary>Unregisters this connection from <paramref name="channel"/>. Returns the remaining subscription count.</summary>
    int Unsubscribe(string channel);

    /// <summary>The connection's current subscription count — the number RESP2 echoes on each (un)subscribe.</summary>
    int Count { get; }
}
