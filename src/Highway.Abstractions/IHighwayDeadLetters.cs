namespace Highway.Abstractions;

/// <summary>
/// Typed operator API for inspecting, requeuing, purging, and counting dead-lettered messages (feature 073).
/// Registered in DI via <c>AddHighway</c> as an independent service.
/// </summary>
public interface IHighwayDeadLetters
{
    /// <summary>
    /// Inspect the oldest dead letters for the contract's target without removing them.
    /// </summary>
    /// <typeparam name="T">The contract type associated with the queue, service, or channel group.</typeparam>
    /// <param name="count">Maximum number of entries to inspect (oldest first). Defaults to 20.</param>
    /// <param name="group">Optional subscription group override for channel contracts. Defaults to this node's subscription group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of dead-letter records, oldest first.</returns>
    Task<IReadOnlyList<DeadLetter<T>>> PeekAsync<T>(int count = 20, string? group = null, CancellationToken ct = default);

    /// <summary>
    /// Count dead letters currently held for the contract's target.
    /// </summary>
    /// <typeparam name="T">The contract type associated with the queue, service, or channel group.</typeparam>
    /// <param name="group">Optional subscription group override for channel contracts. Defaults to this node's subscription group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total count of dead letters currently stored for the target.</returns>
    Task<long> CountAsync<T>(string? group = null, CancellationToken ct = default);

    /// <summary>
    /// Requeue a specific dead letter by its opaque DeadLetterId handle ({box:x8}{seq:x16}).
    /// Returns true if the message was requeued, or false if it was not found or already handled.
    /// Operator requeues bypass MaxRequeues limits but increment the lineage requeue count.
    /// </summary>
    /// <typeparam name="T">The contract type associated with the target.</typeparam>
    /// <param name="deadLetterId">Opaque 24-character hexadecimal dead-letter handle.</param>
    /// <param name="after">Optional delay before the requeued message becomes claimable.</param>
    /// <param name="group">Optional subscription group override for channel contracts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message was requeued; false if not found or already removed.</returns>
    Task<bool> RequeueAsync<T>(string deadLetterId, TimeSpan? after = null, string? group = null, CancellationToken ct = default);

    /// <summary>
    /// Purge a specific dead letter by its opaque DeadLetterId handle ({box:x8}{seq:x16}).
    /// Returns true if the message was purged, or false if it was not found or already handled.
    /// </summary>
    /// <typeparam name="T">The contract type associated with the target.</typeparam>
    /// <param name="deadLetterId">Opaque 24-character hexadecimal dead-letter handle.</param>
    /// <param name="group">Optional subscription group override for channel contracts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message was purged; false if not found or already removed.</returns>
    Task<bool> PurgeAsync<T>(string deadLetterId, string? group = null, CancellationToken ct = default);

    /// <summary>
    /// Requeue up to <paramref name="max"/> oldest dead letters for the contract's target.
    /// Returns the number of messages requeued.
    /// Operator requeues bypass MaxRequeues limits but increment the lineage requeue count.
    /// </summary>
    /// <typeparam name="T">The contract type associated with the target.</typeparam>
    /// <param name="max">Maximum number of dead letters to requeue.</param>
    /// <param name="after">Optional delay before the requeued messages become claimable.</param>
    /// <param name="group">Optional subscription group override for channel contracts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of dead letters requeued.</returns>
    Task<int> RequeueAllAsync<T>(int max, TimeSpan? after = null, string? group = null, CancellationToken ct = default);

    /// <summary>
    /// Purge up to <paramref name="max"/> oldest dead letters for the contract's target.
    /// Returns the number of messages purged.
    /// </summary>
    /// <typeparam name="T">The contract type associated with the target.</typeparam>
    /// <param name="max">Maximum number of dead letters to purge.</param>
    /// <param name="group">Optional subscription group override for channel contracts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of dead letters purged.</returns>
    Task<int> PurgeAllAsync<T>(int max, string? group = null, CancellationToken ct = default);
}
