namespace Highway.Abstractions;

/// <summary>
/// A dead-lettered message carrying payload, diagnosis, and requeue lineage (feature 073).
/// </summary>
/// <typeparam name="T">The contract type associated with the target.</typeparam>
/// <param name="Message">The deserialized contract message, or null when the payload fails to deserialize.</param>
/// <param name="RawPayload">The raw message payload bytes.</param>
/// <param name="Id">The message ID (for queues/channels) or request ID (for services).</param>
/// <param name="DeadLetterId">An opaque handle ({box:x8}{seq:x16}) for point addressability.</param>
/// <param name="Target">The destination queue, service, or subscriber group.</param>
/// <param name="Attempts">How many delivery attempts were made before dying.</param>
/// <param name="Requeues">How many times this message has been requeued previously.</param>
/// <param name="DeadLetteredAt">Timestamp when the message was moved to the dead-letter list.</param>
/// <param name="Reason">Reason code for dead-lettering (e.g., "MAX_ATTEMPTS").</param>
/// <param name="LastFailure">Details of the exception from the most recent delivery attempt, if recorded.</param>
/// <param name="FirstFailure">Details of the initial failure shape if it differed from the final attempt.</param>
public sealed record DeadLetter<T>(
    T? Message,
    ReadOnlyMemory<byte> RawPayload,
    string Id,
    string DeadLetterId,
    DeadLetterTarget Target,
    int Attempts,
    int Requeues,
    DateTimeOffset DeadLetteredAt,
    string Reason,
    FailureInfo? LastFailure,
    FailureInfo? FirstFailure);

/// <summary>Identifies the target where the dead letter occurred.</summary>
public sealed record DeadLetterTarget(DeadLetterTargetKind Kind, string Name, string? Group = null)
{
    public override string ToString()
        => Kind switch
        {
            DeadLetterTargetKind.Queue => $"Q:{Name}",
            DeadLetterTargetKind.Group => $"Q:{Name}@{Group}",
            _ => $"{Kind}:{Name}"
        };
}

/// <summary>Target family kind for a dead letter.</summary>
public enum DeadLetterTargetKind
{
    Queue = 1,
    // 2 was Service: RPC has no dead letters (074) — an expired or failed request is removed and recorded.
    Group = 3
}

/// <summary>Exception diagnostic information recorded by HW.FAIL.</summary>
public sealed record FailureInfo(string Type, string Detail);

/// <summary>
/// The decision returned by an <see cref="IDeadLetter{T}"/> handler.
/// </summary>
public readonly record struct DeadLetterAction
{
    public enum ActionType { Keep = 1, Discard = 2, Requeue = 3 }

    public ActionType Type { get; }
    public TimeSpan? After { get; }

    private DeadLetterAction(ActionType type, TimeSpan? after = null)
    {
        Type = type;
        After = after;
    }

    /// <summary>Leaves the dead letter on the target's DLQ for operator inspection.</summary>
    public static DeadLetterAction Keep { get; } = new(ActionType.Keep);

    /// <summary>Removes the dead letter permanently.</summary>
    public static DeadLetterAction Discard { get; } = new(ActionType.Discard);

    /// <summary>Re-enters the message on its live queue, optionally delayed by <paramref name="after"/>.</summary>
    public static DeadLetterAction Requeue(TimeSpan? after = null) => new(ActionType.Requeue, after);
}
