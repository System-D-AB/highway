namespace Highway.Abstractions;

/// <summary>
/// Structured error information propagated from service to caller.
/// </summary>
public sealed class ErrorDetail
{
    /// <summary>
    /// Machine-readable error code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional stack detail. <b>Never populated for remote callers</b>: a stack trace names
    /// source paths, internal classes and dependency versions, and the wire is not a log file
    /// (concerns.md 9.3). The property remains so existing payloads still deserialize and
    /// local tooling may use it; full traces live in the serving node's own logs.
    /// </summary>
    public string? StackTrace { get; init; }
}
