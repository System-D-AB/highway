using System.Net;

namespace Highway.Server.Resp;

/// <summary>
/// Decides whether a RESP connection is allowed to run commands (040, full impl in T6 / 037 R11).
/// The RESP session holds one of these; the transport hands it the remote endpoint so the loopback
/// exemption and <c>WithoutAuthentication()</c> can short-circuit straight to authenticated.
/// </summary>
internal interface IConnectionAuthenticator
{
    /// <summary>
    /// True when a connection from <paramref name="remote"/> may run commands without an
    /// <c>AUTH</c> handshake — no password configured, authentication explicitly disabled, or a
    /// loopback client (the C6.x exemptions, preserved).
    /// </summary>
    bool IsPreAuthorized(EndPoint? remote);

    /// <summary>
    /// Verifies an <c>AUTH</c> attempt. <paramref name="username"/> is null for the one-argument
    /// <c>AUTH &lt;password&gt;</c> form (the <c>default</c> user). Constant-time compare (037 R11).
    /// </summary>
    bool TryAuthenticate(string? username, string password);
}
