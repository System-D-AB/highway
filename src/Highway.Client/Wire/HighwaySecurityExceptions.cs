namespace Highway.Client.Wire;

/// <summary>
/// The server refused the connection's credentials, or required credentials that were not
/// supplied (feature 012). Corresponds to Garnet's <c>NOAUTH</c> and <c>WRONGPASS</c>.
///
/// <para><b>Permanent.</b> Retrying a wrong password wastes the backoff budget and trips
/// attempt counters on systems that keep them. The remedy is configuration, not patience.</para>
/// </summary>
public sealed class HighwayAuthenticationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The connection authenticated, but the user is not permitted to run the command
/// (feature 012). Corresponds to Garnet's <c>NOPERM</c>.
///
/// <para><b>Permanent</b>, for the same reason as
/// <see cref="HighwayAuthenticationException"/>.</para>
///
/// <para><see cref="Command"/> is attached by the caller, not parsed from the reply:
/// Garnet's message is literally <c>"NOPERM this user has no permissions to run the
/// command"</c> and names nothing. With per-command permissions in play, which command was
/// refused is the entire question, so the one party that knows it supplies it.</para>
/// </summary>
public sealed class HighwayAuthorizationException(string? command, Exception? inner = null)
    : Exception(
        command is null
            ? "The Highway server refused the command: this user has no permission to run it."
            : $"The Highway server refused '{command}': this user has no permission to run it.",
        inner)
{
    /// <summary>The refused command, when the call site supplied it.</summary>
    public string? Command { get; } = command;
}

/// <summary>
/// The message type has no <c>[Queue]</c> registration in this node's catalog (feature 014).
/// Thrown locally before anything touches the network — a send has no response object to
/// carry a status code.
/// </summary>
public sealed class QueueNotRegisteredException(Type messageType)
    : Exception($"The message type '{messageType.FullName}' is not registered as a queue in this node's catalog. " +
                $"Add [Queue(\"name\")] to the message type and ensure the contract assembly is discovered.");
