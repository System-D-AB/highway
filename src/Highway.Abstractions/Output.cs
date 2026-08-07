namespace Highway.Abstractions;

/// <summary>
/// Base class for all RPC responses. Carries status code and error information.
/// </summary>
public abstract class Output
{
    /// <summary>
    /// HTTP-style status code (200, 404, 500, etc.). Null means success (defaulted to 200 by the engine).
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Structured error detail when the service fails.
    /// </summary>
    public ErrorDetail? Error { get; set; }
}
