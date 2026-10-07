namespace Highway.Client.Engine;

/// <summary>
/// Thrown or reported when message processing exceeds MaxProcessingTime.
/// </summary>
internal sealed class ProcessingTimeExceededException : Exception
{
    public ProcessingTimeExceededException(string message) : base(message) { }
    public ProcessingTimeExceededException(string message, Exception inner) : base(message, inner) { }
}
