namespace Highway.Client.Wire;

/// <summary>
/// A permanent wire failure: the server rejected the command with an
/// <c>ERR HW_*</c> validation error, returned an unrecognized error, or the
/// connection failed after bounded retries. Permanent failures are never
/// retried — they would fail identically every time (004.1 error contract).
/// </summary>
public sealed class HighwayTransportException(string message) : Exception(message);

/// <summary>
/// A transient wire failure: the server aborted the transaction with the bare
/// <c>ERR Transaction failed.</c> message — a watch conflict under concurrency
/// (004.1 error contract). The command did no work and is safe to retry; the
/// connection retries it with bounded backoff before surfacing this exception.
/// </summary>
public sealed class HighwayTransientException(string message) : Exception(message);

/// <summary>
/// The message type has no <c>[Channel]</c> registration in this node's catalog.
/// Thrown locally by <c>PublishAsync</c> before anything touches the network —
/// publish has no response object to carry a status code.
/// </summary>
public sealed class ChannelNotRegisteredException(Type messageType)
    : Exception($"The message type '{messageType.FullName}' is not registered as a channel in this node's catalog. " +
                $"Add [Channel(\"name\")] to the message type and ensure an ISubscribe<T> implementation is discovered.");

/// <summary>
/// The serialized envelope exceeds the server's max payload size.
/// Thrown locally by <c>PublishAsync</c> before anything touches the network —
/// publish has no response object to carry a status code.
/// </summary>
public sealed class PayloadTooLargeException(long actual, long limit)
    : Exception($"Envelope is {actual} bytes, exceeding the configured maximum of {limit} bytes.");
