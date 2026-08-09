namespace Highway.Server.Internal;

/// <summary>
/// Stable RESP error codes emitted by Highway commands.
///
/// <para><b>These strings are a stable client contract.</b> Clients (feature 005)
/// classify server errors from the message alone:</para>
/// <list type="bullet">
///   <item>a message starting with <c>ERR HW_</c> is a <b>permanent</b> failure — never retry;</item>
///   <item>the bare <c>ERR Transaction failed.</c> is emitted only by Garnet itself
///         for a transient abort (watch conflict) — safe to retry;</item>
///   <item>anything else is permanent.</item>
/// </list>
///
/// <para>Full message shape: <c>ERR {code} {detail}</c>.</para>
/// </summary>
internal static class HighwayErrors
{
    /// <summary>Prefix shared by every Highway-emitted error message.</summary>
    public const string Prefix = "ERR HW_";

    /// <summary>
    /// Identifier blank, contains a control character, exceeds the length cap,
    /// or is otherwise malformed (e.g. a non-numeric message ID).
    /// </summary>
    /// <summary>
    /// A queue is at its byte limit and refused the message (feature 016).
    ///
    /// <para><b>Permanent</b>, under the 004.1 contract: the connection does not retry it.
    /// A full queue may well drain, which is the argument for transient — and it is the wrong
    /// argument. A bounded client retry would hold a connection and hammer a broker already
    /// over budget, and if the queue does not drain the caller learns nothing until the
    /// retries are exhausted. Backpressure is information the application must act on.</para>
    /// </summary>
    public const string QueueFull = "HW_QUEUE_FULL";

    public const string InvalidArg = "HW_INVALID_ARG";

    /// <summary>Payload exceeds <c>MaxPayloadBytes</c>.</summary>
    public const string PayloadTooLarge = "HW_PAYLOAD_TOO_LARGE";

    /// <summary><c>HW.RECEIVE COUNT</c> non-numeric, zero, negative, overflowing, or above <c>ReceiveMaxCount</c>.</summary>
    public const string InvalidCount = "HW_INVALID_COUNT";

    /// <summary>
    /// Unexpected exception escaped into a command's catch block.
    /// Classified permanent deliberately: ACK/RACK and the lease sweeps rewrite
    /// whole lists, so a mid-loop failure can leave partial state that retrying
    /// would compound rather than repair.
    /// </summary>
    /// <summary>
    /// A queue holds entries written by a pre-013 Highway. Permanent: retrying cannot
    /// help, and the remedy is to drain the queue or delete the data directory.
    /// </summary>
    public const string StorageFormat = "HW_STORAGE_FORMAT";

    public const string Internal = "HW_INTERNAL";

    /// <summary>Formats <c>ERR {code} {detail}</c>.</summary>
    public static string Format(string code, string detail) => $"ERR {code} {detail}";

    /// <summary>Formats an <see cref="InvalidArg"/> error.</summary>
    public static string InvalidArgError(string detail) => Format(InvalidArg, detail);

    /// <summary>Formats a <see cref="PayloadTooLarge"/> error naming actual and limit.</summary>
    public static string PayloadTooLargeError(long actual, long limit)
        => Format(PayloadTooLarge, $"{actual} > {limit}");

    /// <summary>Formats an <see cref="InvalidCount"/> error.</summary>
    public static string InvalidCountError(string detail) => Format(InvalidCount, detail);

    /// <summary>Formats an <see cref="Internal"/> error.</summary>
    public static string InternalError(string detail) => Format(Internal, detail);

    /// <summary>Formats the pre-013 storage-format refusal.</summary>
    public static string StorageFormatError(string detail) => Format(StorageFormat, detail);
}
