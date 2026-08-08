using System.Text.Json;
using Highway.Abstractions;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// One loop per catalog service. <see cref="SingleMessageWorkerLoop"/> supplies the wake,
/// the concurrency gate, the drain and the in-flight tracking; this class supplies what is
/// specific to RPC — claim with <c>HW.DEQUEUE</c>, then parse envelope → run the service via
/// <see cref="ServiceExecutor"/> → <c>HW.REPLY</c> BEFORE <c>HW.ACK</c>.
/// </summary>
internal sealed class RpcWorkerLoop : SingleMessageWorkerLoop
{
    private readonly ServiceDescriptor _descriptor;

    public RpcWorkerLoop(
        ServiceDescriptor descriptor,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string nodeName,
        int concurrency,
        LoopWake wake,
        ILogger logger)
        : base(descriptor.RequestType, connection, executor, nodeName, concurrency, wake, logger)
    {
        _descriptor = descriptor;
    }

    public string ServiceName => _descriptor.Name;

    protected override string TargetName => _descriptor.Name;
    protected override string TargetKind => "service";

    protected override async Task<(string Id, byte[] Payload)?> ClaimAsync(CancellationToken stopToken)
    {
        var item = await Connection.DequeueAsync(_descriptor.Name, NodeName, stopToken).ConfigureAwait(false);
        return item is null ? null : (item.Value.RequestId, item.Value.Payload);
    }

    protected override void LogProcessingFailure(Exception ex, string id)
        => Logger.LogError(ex, "Unhandled error processing request '{RequestId}' for service '{Service}'",
            id, _descriptor.Name);

    protected override async Task ProcessAsync(string requestId, byte[] payload, CancellationToken ct)
    {
        object? request = null;
        Output result;

        try
        {
            var envelope = HighwayJson.DecodeEnvelope(payload);
            request = HighwayJson.DeserializeBody(envelope, _descriptor.RequestType);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Poison envelope for request '{RequestId}' on '{Service}'; replying 400 and acking",
                requestId, _descriptor.Name);
            result = new GenericOutput
            {
                StatusCode = StatusCodes.Status400BadRequest,
                Error = new ErrorDetail
                {
                    Code = "BAD_ENVELOPE",
                    Message = "The request envelope could not be parsed.",
                },
            };
            await ReplyThenAckAsync(requestId, result, ct).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            result = new GenericOutput
            {
                StatusCode = StatusCodes.Status400BadRequest,
                Error = new ErrorDetail { Code = "BAD_ENVELOPE", Message = "The request body deserialized to null." },
            };
            await ReplyThenAckAsync(requestId, result, ct).ConfigureAwait(false);
            return;
        }

        // Deduplication (feature 013). Only for contracts that asked for it; everything
        // else takes exactly the path it always did.
        if (IdempotencyWindow is { } window)
        {
            var claim = await Connection
                .ClaimIdempotencyAsync(_descriptor.Name, requestId, window, ct).ConfigureAwait(false);

            switch (claim.Outcome)
            {
                case IdempotencyOutcome.Duplicate:
                    // The handler already ran for this delivery. Reply with what it
                    // produced, so the caller is unaffected by the duplication, and ack so
                    // the redelivery stops.
                    Logger.LogDebug(
                        "Suppressed a duplicate delivery of '{RequestId}' on '{Service}'; replying with the original response",
                        requestId, _descriptor.Name);
                    await ReplyRawThenAckAsync(requestId, claim.Response!, ct).ConfigureAwait(false);
                    return;

                case IdempotencyOutcome.InProgress:
                    // Another attempt holds the claim — running now, or crashed while
                    // running. Neither run nor reply nor ack: the lease expires and the
                    // request is redelivered after the window. Re-running on a stale
                    // marker would break the only promise [Idempotent] makes.
                    Logger.LogDebug(
                        "Delivery of '{RequestId}' on '{Service}' is already in progress; leaving it for lease recovery",
                        requestId, _descriptor.Name);
                    return;
            }
        }

        var raw = await Executor.ExecuteServiceAsync(_descriptor.Name, request, ct).ConfigureAwait(false);

        result = raw as Output ?? new GenericOutput
        {
            StatusCode = StatusCodes.Status500InternalServerError,
            Error = new ErrorDetail { Code = "INTERNAL_ERROR", Message = "The service returned a non-Output result." },
        };

        var envelopeBytes = HighwayJson.EncodeEnvelope(NodeName, result);

        if (IdempotencyWindow is { } completeWindow)
        {
            // Replace the in-progress marker with the response before replying, so a
            // redelivery that arrives during the reply finds an answer rather than a
            // marker it must wait out.
            await Connection
                .CompleteIdempotencyAsync(_descriptor.Name, requestId, envelopeBytes, completeWindow, ct)
                .ConfigureAwait(false);
        }

        await ReplyRawThenAckAsync(requestId, envelopeBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the reply envelope, THEN acks — a crash between the two still delivers the response.</summary>
    private Task ReplyThenAckAsync(string requestId, Output result, CancellationToken ct)
        => ReplyRawThenAckAsync(requestId, HighwayJson.EncodeEnvelope(NodeName, result), ct);

    /// <summary>
    /// As <see cref="ReplyThenAckAsync"/>, but for an envelope that already exists — the
    /// cached response of a suppressed duplicate, which must be returned byte-for-byte
    /// rather than re-encoded.
    /// </summary>
    private async Task ReplyRawThenAckAsync(string requestId, byte[] responseEnvelope, CancellationToken ct)
    {
        try
        {
            await Connection.ReplyAsync(requestId, responseEnvelope, ct).ConfigureAwait(false);
        }
        catch (HighwayTransportException ex)
        {
            Logger.LogError(ex, "Permanent error replying to '{RequestId}' on '{Service}'; not acking",
                requestId, _descriptor.Name);
            return; // do not ack — lease recovery will redeliver
        }

        try
        {
            await Connection.AckAsync(_descriptor.Name, NodeName, requestId, ct).ConfigureAwait(false);
        }
        catch (HighwayTransportException ex)
        {
            // Reply already delivered; a failed ack means possible duplicate execution
            // on redelivery (at-least-once). Log loudly.
            Logger.LogError(ex, "Reply sent but ACK failed for '{RequestId}' on '{Service}' (at-least-once duplicate risk)",
                requestId, _descriptor.Name);
        }
    }
}
