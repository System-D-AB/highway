using System.Text.Json;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// Claims and processes pub/sub work for one channel group through the unified queue engine
/// (feature 018, Phase 1).
///
/// <para>Structurally identical to <see cref="QueueWorkerLoop"/> — claim with
/// <c>HW.QCLAIM</c>, dispatch, acknowledge with <c>HW.QACK</c> — but:
/// <list type="bullet">
///   <item>Uses <see cref="ChannelDescriptor"/> instead of <see cref="QueueDescriptor"/>.</item>
///   <item>Dispatches to ALL local subscribers via <see cref="ServiceExecutor.ExecuteSubscribersAsync"/>
///         instead of a single processor.</item>
///   <item>Uses the derived queue name <c>{channel}@{group}</c> for claim/ack, where the
///         group is the node's <c>SubscriptionGroup</c> (feature 025) — <c>NodeName</c> by
///         default. <b>The claimant IS the group</b>: replicas sharing a group compete
///         through one queue and one processing list, which is exactly what keeps every
///         group key derivable from <c>{channel}@{group}</c> and therefore declarable in
///         the server's <c>Prepare</c>.</item>
///   <item>Does NOT use idempotency — pub/sub messages carry no <c>[Idempotent]</c> attribute.</item>
/// </list>
/// </para>
///
/// <para><b>A handler that throws is not acknowledged.</b> The message stays in the
/// processing list, its lease expires, and it is redelivered — which is the whole meaning
/// of at-least-once. This is the queue engine's semantic, adopted for subscribers by
/// design Decision 5 (018).</para>
/// </summary>
internal sealed class SubscriptionWorkerLoop : SingleMessageWorkerLoop
{
    private readonly ChannelDescriptor _descriptor;
    private readonly string _derivedQueueName;

    public SubscriptionWorkerLoop(
        ChannelDescriptor descriptor,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string group,
        int concurrency,
        LoopWake wake,
        ILogger logger,
        TimeSpan renewalInterval = default,
        TimeSpan maxProcessingTime = default)
        // The base's identity is the GROUP, not the physical node (025): every wire call this
        // loop makes — claim, ack, touch, fail — must name the same party, or lease renewal
        // and dead letters would target a processing list that no claim ever wrote to.
        : base(descriptor.MessageType, connection, executor, group, concurrency, wake, logger, renewalInterval, maxProcessingTime)
    {
        _descriptor = descriptor;
        _derivedQueueName = $"{descriptor.Name}@{group}";
    }

    public string ChannelName => _descriptor.Name;

    public string DerivedQueueName => _derivedQueueName;

    /// <summary>
    /// Reported against the <b>derived queue</b>, not the channel. <c>HW.FAIL Q</c> locks
    /// <c>hw:q:{name}:proc:{node}</c>, and the processing list this loop actually claims from
    /// is the group queue's — naming the bare channel targets a key that does not exist, so the
    /// report returns <c>:0</c> and the dead letter says "not reported" while looking healthy.
    /// </summary>
    protected override FailureTarget Target => new(FailureFamily.Channel, _derivedQueueName, NodeName);

    // Deliberately not acknowledged: the message returns after its lease expires, and
    // eventually dead-letters if it can never succeed. This is the queue semantic adopted
    // for subscribers by design Decision 5 (018).
    protected override string FailureDisposition => "it will be redelivered";

    protected override async Task<(string Id, byte[] Payload)?> ClaimAsync(CancellationToken stopToken)
    {
        var claimed = await Connection.QClaimAsync(_derivedQueueName, NodeName, stopToken).ConfigureAwait(false);
        return claimed is null ? null : (claimed.Value.MessageId, claimed.Value.Payload);
    }

    protected override async Task ProcessAsync(string messageId, byte[] payload, CancellationToken ct)
    {
        object? message;
        try
        {
            var envelope = HighwayJson.DecodeEnvelope(payload);
            message = HighwayJson.DeserializeBody(envelope, _descriptor.MessageType);
        }
        catch (JsonException ex)
        {
            // Poison message: cannot parse, cannot retry. Leave for the lease sweep to
            // dead-letter — same behaviour as QueueWorkerLoop.
            Logger.LogError(ex,
                "Message '{MessageId}' on channel '{Channel}' (queue '{Queue}') could not be deserialized; leaving it to dead-letter",
                messageId, _descriptor.Name, _derivedQueueName);
            return;
        }

        if (message is null)
        {
            Logger.LogError(
                "Message '{MessageId}' on channel '{Channel}' (queue '{Queue}') deserialized to null; leaving it to dead-letter",
                messageId, _descriptor.Name, _derivedQueueName);
            return;
        }

        // Deduplication (013), keyed on the DERIVED QUEUE so one group's suppression cannot
        // hide another's delivery. This is the remedy R5.4 promises for the cost it introduces:
        // a failing sibling forces a redelivery that re-runs the siblings that already
        // succeeded, and [Idempotent] is how a handler that cannot tolerate that opts out.
        // Before 018 the attribute was silently ignored on ISubscribe<T> — the batch loop had
        // no gate at all — so this is a new capability, not a preserved one.
        if (IdempotencyWindow is { } window)
        {
            var claim = await Connection
                .ClaimIdempotencyAsync(_derivedQueueName, messageId, window, ct).ConfigureAwait(false);

            switch (claim.Outcome)
            {
                case IdempotencyOutcome.Duplicate:
                    Logger.LogDebug(
                        "Suppressed a duplicate delivery of '{MessageId}' on channel '{Channel}' (queue '{Queue}')",
                        messageId, _descriptor.Name, _derivedQueueName);
                    await Connection.QAckAsync(_derivedQueueName, NodeName, messageId, ct).ConfigureAwait(false);
                    return;

                case IdempotencyOutcome.InProgress:
                    // Another attempt holds the claim, or held it when its process died.
                    // Neither dispatch nor acknowledge: re-running on a stale marker would
                    // break the only promise [Idempotent] makes.
                    Logger.LogDebug(
                        "Delivery of '{MessageId}' on channel '{Channel}' is already in progress",
                        messageId, _descriptor.Name);
                    return;
            }
        }

        // Sequential fan-out to all local subscribers. If any throw, the exception
        // propagates to the base class, which reports the failure and does NOT acknowledge —
        // the message is redelivered after lease expiry (design Decision 5).
        await Executor.ExecuteSubscribersAsync(_descriptor.Name, message, ct).ConfigureAwait(false);

        if (IdempotencyWindow is { } completeWindow)
        {
            // A subscription has no response to cache, so the marker records completion only.
            await Connection
                .CompleteIdempotencyAsync(_derivedQueueName, messageId, [1], completeWindow, ct)
                .ConfigureAwait(false);
        }

        // All subscribers succeeded — acknowledge.
        await Connection.QAckAsync(_derivedQueueName, NodeName, messageId, ct).ConfigureAwait(false);
    }
}
