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
    /// Optional inner exception details (for debugging, not for production clients).
    /// </summary>
    public string? StackTrace { get; init; }
}
