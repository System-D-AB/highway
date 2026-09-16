using System.IO.Pipelines;

namespace Highway.Server.Resp;

/// <summary>
/// What a <see cref="RespConnectionHandler"/> needs from the server it runs inside (040 T4/T7):
/// the command dispatcher, the authenticator, the frame-size bound, and the doorbell subscription
/// registry. Keeping this a seam lets the handler be constructed in a test with fakes and lets the
/// real server (T8's embedded host) supply the live wiring.
/// </summary>
internal interface IRespServerHost
{
    /// <summary>The name→command dispatcher (the served subset).</summary>
    CommandDispatcher Dispatcher { get; }

    /// <summary>The connection authenticator (loopback/exemption + AUTH verification).</summary>
    IConnectionAuthenticator Authenticator { get; }

    /// <summary>The largest RESP frame the reader will accept before closing the connection (aligned with the payload cap).</summary>
    int MaxFrameBytes { get; }

    /// <summary>
    /// Registers a connection with the doorbell subscription registry and returns its per-connection
    /// sink. <paramref name="output"/> is where a matching doorbell publish writes its push frame.
    /// </summary>
    ISubscriptionSink CreateSubscriber(string connectionId, PipeWriter output);

    /// <summary>Drops a connection from the registry on teardown.</summary>
    void RemoveSubscriber(string connectionId);

    /// <summary>The node-name → observed-peer-address map (048), fed by <c>CLIENT SETNAME</c>.</summary>
    ObservedAddressRegistry ObservedAddresses { get; }
}
