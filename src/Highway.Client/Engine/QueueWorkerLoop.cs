using System.Text.Json;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// Claims and processes queued work for one queue (feature 014).
///
/// <para>The RPC worker loop without the reply: claim, deserialize, process, acknowledge.
/// Everything up to "process" lives in <see cref="SingleMessageWorkerLoop"/>. Multiple
/// instances of the application run this concurrently against the same queue and
/// <b>compete</b> — that is what makes a queue a queue.</para>
///
/// <para><b>A handler that throws is not acknowledged.</b> The message stays in the
/// processing list, its lease expires, and it is redelivered — which is the whole meaning
/// of at-least-once. Swallowing the exception and acknowledging would silently discard
/// work the sender believes is being done.</para>
/// </summary>
internal sealed class QueueWorkerLoop : SingleMessageWorkerLoop
{
    private readonly QueueDescriptor _descriptor;

    public QueueWorkerLoop(
        QueueDescriptor descriptor,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string nodeName,
        int concurrency,
        LoopWake wake,
        ILogger logger,
        TimeSpan renewalInterval = default,
        TimeSpan maxProcessingTime = default)
        : base(descriptor.MessageType, connection, executor, nodeName, concurrency, wake, logger, renewalInterval, maxProcessingTime)
    {
        _descriptor = descriptor;
    }

    public string QueueName => _descriptor.Name;

    protected override FailureTarget Target => new(FailureFamily.Queue, _descriptor.Name, NodeName);

    // Deliberately not acknowledged: the message returns after its lease expires, and
    // eventually dead-letters if it can never succeed.
    protected override string FailureDisposition => "it will be redelivered";

    protected override async Task<(string Id, byte[] Payload)?> ClaimAsync(CancellationToken stopToken)
    {
        var claimed = await Connection.QClaimAsync(_descriptor.Name, NodeName, stopToken).ConfigureAwait(false);
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
            // A queue has no caller to answer with a 400, and retrying would loop on a
            // payload that can never parse. Acknowledging would discard it silently, so
            // it is left for the lease sweep to dead-letter.
            Logger.LogError(ex,
                "Message '{MessageId}' on queue '{Queue}' could not be deserialized; leaving it to dead-letter",
                messageId, _descriptor.Name);
            return;
        }

        if (message is null)
        {
            Logger.LogError("Message '{MessageId}' on queue '{Queue}' deserialized to null; leaving it to dead-letter",
                messageId, _descriptor.Name);
            return;
        }

        if (IdempotencyWindow is { } window)
        {
            var claim = await Connection
                .ClaimIdempotencyAsync(_descriptor.Name, messageId, window, ct).ConfigureAwait(false);

            switch (claim.Outcome)
            {
                case IdempotencyOutcome.Duplicate:
                    Logger.LogDebug("Suppressed a duplicate delivery of '{MessageId}' on queue '{Queue}'",
                        messageId, _descriptor.Name);
                    await Connection.QAckAsync(_descriptor.Name, NodeName, messageId, ct).ConfigureAwait(false);
                    return;

                case IdempotencyOutcome.InProgress:
                    // Another attempt holds the claim, or held it when its process died.
                    // Neither run nor acknowledge: re-running on a stale marker would break
                    // the only promise [Idempotent] makes.
                    Logger.LogDebug("Delivery of '{MessageId}' on queue '{Queue}' is already in progress",
                        messageId, _descriptor.Name);
                    return;
            }
        }

        await Executor.ExecuteProcessorAsync(_descriptor.Name, message, ct).ConfigureAwait(false);

        if (IdempotencyWindow is { } completeWindow)
        {
            // A queue has no response to cache, so the marker simply records completion.
            await Connection
                .CompleteIdempotencyAsync(_descriptor.Name, messageId, [1], completeWindow, ct)
                .ConfigureAwait(false);
        }

        await Connection.QAckAsync(_descriptor.Name, NodeName, messageId, ct).ConfigureAwait(false);
    }
}
