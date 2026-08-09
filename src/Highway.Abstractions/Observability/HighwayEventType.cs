namespace Highway.Abstractions.Observability;

/// <summary>
/// What a recorded event represents. Each value names the command that produces
/// it, so the recorder's contents can be traced back to the protocol.
///
/// <para><b>Not every command produces an event.</b> Liveness heartbeats are
/// excluded: feature 006 made them fire every five seconds per node, so
/// recording them would evict real history to store the fact that nothing
/// happened. Read-only commands (<c>HW.DISCOVER</c>, <c>HW.STATS</c>,
/// <c>HW.REPLAY</c>) are excluded for the same reason — recording reads would
/// drown the record, and querying it would record the query.</para>
/// </summary>
public enum HighwayEventType
{
    /// <summary><c>HW.CALL</c> — an RPC request was enqueued.</summary>
    RpcEnqueued = 0,

    /// <summary><c>HW.DEQUEUE</c> — a node claimed a request. Not recorded when the queue was empty.</summary>
    RpcClaimed = 1,

    /// <summary><c>HW.REPLY</c> — a response was written to the caller's reply slot.</summary>
    RpcReplied = 2,

    /// <summary><c>HW.ACK</c> — a claimed request was acknowledged.</summary>
    RpcAcknowledged = 3,

    /// <summary><c>HW.PUBLISH</c> — a message was fanned out. <c>Count</c> carries the group count.</summary>
    Published = 4,

    /// <summary><c>HW.SUBSCRIBE</c> — a subscriber group was registered.</summary>
    GroupRegistered = 5,

    /// <summary><c>HW.UNSUBSCRIBE</c> — a subscriber group and its pending state were removed.</summary>
    GroupRemoved = 6,

    /// <summary>
    /// <c>HW.RECEIVE</c> — a batch was consumed. One event per batch, not per
    /// message: a batch of 500 is one operation.
    /// <c>Count</c> carries the batch size.
    /// </summary>
    MessagesReceived = 7,

    /// <summary><c>HW.RACK</c> — a consumed message was acknowledged.</summary>
    MessageAcknowledged = 8,

    /// <summary><c>HW.HEARTBEAT</c> registration form — a node announced its catalog.</summary>
    NodeRegistered = 9,

    /// <summary><c>HW.HEARTBEAT &lt;node&gt; BYE</c> — a node announced departure.</summary>
    NodeDeparted = 10,

    /// <summary>
    /// A request exhausted <c>MaxDeliveryAttempts</c> and was moved to the service's
    /// dead-letter list instead of being requeued (feature 013).
    /// <c>Count</c> carries the attempt count, <c>ErrorCode</c> the reason.
    ///
    /// <para>Recorded because dead-lettering makes a previously loud failure quiet: a
    /// message that used to loop visibly now leaves the queue once. If nothing records
    /// that, the fix has traded an obvious bug for a silent one.</para>
    /// </summary>
    RpcDeadLettered = 11,

    /// <summary>
    /// A pub/sub message exhausted <c>MaxDeliveryAttempts</c> for one group and was moved
    /// to that group's dead-letter list instead of being redelivered (feature 013).
    /// <c>Count</c> carries the attempt count, <c>ErrorCode</c> the reason.
    /// </summary>
    MessageDeadLettered = 12,

    /// <summary><c>HW.QSEND</c> - work was enqueued (feature 014).</summary>
    QueueSent = 13,

    /// <summary><c>HW.QCLAIM</c> - a worker claimed queued work. A nil claim records nothing.</summary>
    QueueClaimed = 14,

    /// <summary><c>HW.QACK</c> - claimed work was acknowledged.</summary>
    QueueAcknowledged = 15,

    /// <summary>
    /// A queued message exhausted <c>MaxDeliveryAttempts</c> and was moved to the queue's
    /// dead-letter list. <c>Count</c> carries the attempt count.
    /// </summary>
    QueueDeadLettered = 16,

    /// <summary>
    /// <c>HW.FAIL</c> - a handler threw and said so (feature 015). Recorded for every attempt,
    /// not only the last, so "failed five times then recovered" is visible in the replay rather
    /// than invisible. <c>ErrorCode</c> carries the exception type.
    /// </summary>
    DeliveryFailed = 17,

    /// <summary>
    /// A subscriber group was retired and its queue destroyed (feature 017). <c>Count</c>
    /// carries the number of messages discarded.
    ///
    /// <para>This is the largest single loss Highway can inflict, so it is recorded whether it
    /// was asked for (<c>BYE PURGE</c>) or decided by the broker after a node stayed absent
    /// past the retirement threshold.</para>
    /// </summary>
    GroupRetired = 18,
}
