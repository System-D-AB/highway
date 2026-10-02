namespace Highway.Abstractions;

/// <summary>
/// Configures retry attempts, backoff schedules, and requeue bounds on a contract (feature 073).
/// Can be applied to <see cref="ISend"/> (queue), <see cref="IPublish"/> (channel), or <see cref="IReturn{T}"/> (RPC service).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RetryAttribute : Attribute
{
    /// <summary>
    /// Maximum delivery attempts before the message is dead-lettered.
    /// When 0 or omitted, the server option <c>MaxDeliveryAttempts</c> is used.
    /// </summary>
    public int MaxAttempts { get; init; }

    /// <summary>
    /// Backoff strategy for retries between attempts:
    /// <list type="bullet">
    ///   <item><c>"none"</c> (default): immediate redelivery upon claim expiry.</item>
    ///   <item><c>"fixed:30s"</c>: fixed duration wait between attempts.</item>
    ///   <item><c>"exp:1s..5m"</c> or <c>"exp:1s..5m jitter"</c>: exponential backoff doubling between min and max.</item>
    /// </list>
    /// </summary>
    public string Backoff { get; init; } = "none";

    /// <summary>
    /// Maximum number of times an <see cref="IDeadLetter{T}"/> handler may automatically requeue a message.
    /// Requeues past this cap are refused and the letter stays dead. Default is 3.
    /// </summary>
    public int MaxRequeues { get; init; } = 3;
}
