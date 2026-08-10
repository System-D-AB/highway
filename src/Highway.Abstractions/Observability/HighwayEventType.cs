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
    /// <remarks>
    /// <b>Never recorded since feature 018.</b> HW.RECEIVE was removed when pub/sub unified onto
    /// the queue engine; a subscriber group's dead letters now arrive as
    /// <see cref="QueueDeadLettered"/> like any other queue's. The value is kept rather than
    /// deleted because it is documented protocol surface, and reusing the number later would
    /// make an old replay mean something new.
    /// </remarks>
    MessagesReceived = 7,

    /// <summary><c>HW.RACK</c> — a consumed message was acknowledged.</summary>
    /// <remarks>
    /// <b>Never recorded since feature 018.</b> HW.RACK was removed when pub/sub unified onto
    /// the queue engine; a subscriber group's dead letters now arrive as
    /// <see cref="QueueDeadLettered"/> like any other queue's. The value is kept rather than
    /// deleted because it is documented protocol surface, and reusing the number later would
    /// make an old replay mean something new.
    /// </remarks>
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
    /// <remarks>
    /// <b>Never recorded since feature 018.</b> the pub/sub dead-letter path was removed when pub/sub unified onto
    /// the queue engine; a subscriber group's dead letters now arrive as
    /// <see cref="QueueDeadLettered"/> like any other queue's. The value is kept rather than
    /// deleted because it is documented protocol surface, and reusing the number later would
    /// make an old replay mean something new.
    /// </remarks>
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

    /// <summary>
    /// A subscriber group's node has been absent past <b>half</b> the retirement threshold
    /// (feature 017). Nothing has been destroyed yet — this is the warning that precedes it.
    ///
    /// <para>Deliberately an event rather than a stored "suspect" state: an operator replaying
    /// a channel that later went quiet sees the warning that came before, which is most of the
    /// value of a state machine with none of its maintenance.</para>
    /// </summary>
    NodeSuspect = 19,

    /// <summary>
    /// A lease renewal was rejected (feature 019). <b>Successful renewals are not recorded</b> —
    /// at a one-minute interval across many in-flight messages they would flood the recorder
    /// with the least interesting thing it could hold.
    /// </summary>
    LeaseRenewed = 20,

    /// <summary>
    /// A handler exceeded <c>MaxProcessingTime</c> and its lease is no longer being renewed
    /// (feature 019). The message now returns to ordinary lease recovery: requeued, attempt
    /// incremented, eventually dead-lettered.
    ///
    /// <para>Recorded because a handler that routinely exhausts its cap is either mis-sized or
    /// hung, and both are worth knowing before the dead letter appears.</para>
    /// </summary>
    /// <remarks>
    /// <b>Never recorded, and it cannot be without new protocol surface.</b> The cap is reached
    /// <i>client-side</i>, inside the worker loop, and the flight recorder is a server-side
    /// facility that only ever sees what crosses the wire. When renewal stops, nothing is sent —
    /// so there is no command for the server to record. Feature 019 R3.3 asked for this event
    /// and the requirement was not satisfiable as written; the cap is surfaced client-side
    /// instead, at Warning and on the client's <c>ActivitySource</c>.
    /// </remarks>
    ProcessingCapExceeded = 21,

    /// <summary>
    /// A send or publish was refused because a queue was at its byte limit (feature 016).
    /// <c>ErrorCode</c> names the queue, or for a publish the group whose queue was full.
    ///
    /// <para>Counted as well as recorded: a limit nobody can observe being hit is a limit that
    /// gets blamed on the network.</para>
    /// </summary>
    SendRefused = 22,

    /// <summary>
    /// A recurring-job schedule fired: exactly one occurrence message was enqueued and the
    /// schedule re-armed (feature 028). <c>Name</c> is the queue, <c>RequestId</c> the
    /// occurrence's message id — which is what joins the fired message's timeline to its
    /// schedule.
    /// </summary>
    JobFired = 23,

    /// <summary>A schedule was registered or its expression changed (028, OD5: last wins, loudly).</summary>
    JobScheduleChanged = 24,

    /// <summary>A schedule was removed (028). Removal is loud, like every destruction.</summary>
    JobScheduleRemoved = 25,

    /// <summary>
    /// A due schedule could NOT fire because the target queue is at its byte limit (028).
    /// The occurrence is not consumed: nextFire is unchanged and the fire retries on a later
    /// poll — backpressure reaches the scheduler instead of being absorbed silently.
    /// </summary>
    JobFireRefused = 26,
}
