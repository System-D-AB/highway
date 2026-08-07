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
}
