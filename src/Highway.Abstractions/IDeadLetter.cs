namespace Highway.Abstractions;

/// <summary>
/// Handles dead letters for contract type <typeparamref name="T"/> (feature 073).
/// Handlers decide the disposition of dead letters by returning a <see cref="DeadLetterAction"/>:
/// <see cref="DeadLetterAction.Keep"/> (leave on the DLQ for operator inspection),
/// <see cref="DeadLetterAction.Discard"/> (remove the dead letter), or
/// <see cref="DeadLetterAction.Requeue"/> (re-enter the message on its live queue, capped by <see cref="RetryAttribute.MaxRequeues"/>).
/// </summary>
/// <typeparam name="T">A contract type: an <see cref="ISend"/> (queue), <see cref="IPublish"/> (group subscriber), or <see cref="IReturn{T}"/> (RPC service).</typeparam>
public interface IDeadLetter<T> where T : class
{
    /// <summary>
    /// Processes a dead letter and returns the desired disposition.
    /// A throwing handler is safely treated as <see cref="DeadLetterAction.Keep"/> to preserve the dead letter.
    /// </summary>
    Task<DeadLetterAction> HandleAsync(DeadLetter<T> letter, CancellationToken ct = default);
}
