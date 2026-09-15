namespace Highway.Server.Resp;

/// <summary>
/// The connection state machine (040 design §"The connection state machine"). A RESP connection
/// moves Unauthenticated → Authenticated → (Subscribed), and what it may send depends on where
/// it is:
///
/// <list type="table">
///   <item><term>Unauthenticated</term><description><c>AUTH</c>, <c>PING</c> (and the connect-time
///     probes SE.Redis needs before it will call anything a client wrote); everything else gets
///     <c>-NOAUTH</c> naming the fix (037 R11.3).</description></item>
///   <item><term>Authenticated</term><description>the handshake subset + <c>HW.*</c> +
///     <c>SUBSCRIBE</c>; anything outside the subset errors, naming it (037 R6.3).</description></item>
///   <item><term>Subscribed</term><description>only <c>SUBSCRIBE</c>/<c>UNSUBSCRIBE</c>/<c>PING</c>
///     (RESP2 restricted mode); a command outside that set errors per RESP2.</description></item>
/// </list>
///
/// <para>Loopback and <c>WithoutAuthentication()</c> short-circuit straight to
/// <see cref="Authenticated"/> — the C6.x behaviour, preserved (040 design).</para>
/// </summary>
internal enum ConnectionState
{
    Unauthenticated,
    Authenticated,
    Subscribed,
}
