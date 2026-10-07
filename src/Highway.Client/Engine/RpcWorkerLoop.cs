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
        ILogger logger,
        TimeSpan renewalInterval = default,
        TimeSpan maxProcessingTime = default,
        TimeSpan abandonGrace = default,
        int maxAbandonedHandlers = 0)
        : base(descriptor.RequestType, connection, executor, nodeName, concurrency, wake, logger, renewalInterval, maxProcessingTime, abandonGrace, maxAbandonedHandlers)
    {
        _descriptor = descriptor;
    }

    public string ServiceName => _descriptor.Name;

    protected override FailureTarget Target => new(FailureFamily.Service, _descriptor.Name, NodeName);

    protected override string FailureDisposition
        => "it was not acknowledged, so lease recovery will redeliver it";

    protected override Task<ClaimedWork?> ClaimAsync(CancellationToken stopToken)
        => Connection.ClaimAsync(WorkKind.Service, _descriptor.Name, NodeName, stopToken);

    protected override async Task ProcessAsync(ClaimedWork work, CancellationToken ct)
    {
        var (token, requestId, payload) = work;
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
            await ReplyThenAckAsync(requestId, token, result, ct).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            result = new GenericOutput
            {
                StatusCode = StatusCodes.Status400BadRequest,
                Error = new ErrorDetail { Code = "BAD_ENVELOPE", Message = "The request body deserialized to null." },
            };
            await ReplyThenAckAsync(requestId, token, result, ct).ConfigureAwait(false);
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
                    await ReplyRawThenAckAsync(requestId, token, claim.Response!, ct).ConfigureAwait(false);
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

        // The executor turns a handler that stopped on its cancellation token into a 408 CANCELLED result.
        // When that token was the MaxProcessingTime cap (not shutdown), the handler ended BECAUSE of the cap:
        // route it to the 504 path. Any other result the handler returned is answered as-is, even if the cap
        // passed while it finished.
        if (ct.IsCancellationRequested && raw is Output { StatusCode: StatusCodes.Status408RequestTimeout, Error.Code: "CANCELLED" })
            throw new OperationCanceledException(ct);

        result = raw as Output ?? new GenericOutput
        {
            StatusCode = StatusCodes.Status500InternalServerError,
            Error = new ErrorDetail { Code = "INTERNAL_ERROR", Message = "The service returned a non-Output result." },
        };

        byte[] envelopeBytes;
        try
        {
            envelopeBytes = HighwayJson.EncodeEnvelope(NodeName, result);
        }
        catch (Exception ex)
        {
            // The handler HAS RUN. Before this boundary existed, an unserializable response --
            // a cyclic object graph, an unsupported type -- threw past the ack, so the request
            // was redelivered and the already-completed business operation ran again
            // (concerns.md 9.2). A serialization fault is a fault in the ANSWER, not the work;
            // the caller gets a deterministic error and the request is acknowledged below,
            // exactly as if the service had returned that error itself.
            Logger.LogError(ex,
                "Service '{Service}' produced a response of type {ResponseType} that could not be " +
                "serialized (request {RequestId}); replying with RESPONSE_SERIALIZATION_FAILED instead " +
                "of redelivering — the handler already ran",
                _descriptor.Name, result.GetType().Name, requestId);

            envelopeBytes = HighwayJson.EncodeEnvelope(NodeName, new GenericOutput
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                Error = new ErrorDetail
                {
                    Code = "RESPONSE_SERIALIZATION_FAILED",
                    Message = $"The service ran, but its response ({result.GetType().Name}) could not " +
                              $"be serialized: {ex.Message}",
                },
            });
        }

        if (IdempotencyWindow is { } completeWindow)
        {
            // Replace the in-progress marker with the response before replying, so a
            // redelivery that arrives during the reply finds an answer rather than a
            // marker it must wait out. Recorded even when the handler was abandoned (074): the
            // redelivery then replies with this answer instead of waiting until it is dead-lettered.
            await Connection
                .CompleteIdempotencyAsync(_descriptor.Name, requestId, envelopeBytes, completeWindow, CancellationToken.None)
                .ConfigureAwait(false);
        }

        // Abandoned (074): the request was given up and will be redelivered — answer nothing here.
        if (IsAbandoned(ct)) return;

        // Answered with CancellationToken.None: the handler has run; its token may be the expired cap.
        await ReplyRawThenAckAsync(requestId, token, envelopeBytes, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The handler stopped at MaxProcessingTime: the caller gets a 504, and the request is settled.</summary>
    protected override Task OnProcessingTimeExceededAsync(ClaimedWork work, CancellationToken workToken)
        => ReplyThenAckAsync(work.Id, work.Token, new GenericOutput
        {
            StatusCode = StatusCodes.Status504GatewayTimeout,
            Error = new ErrorDetail { Code = "PROCESSING_TIME_EXCEEDED", Message = "processing exceeded MaxProcessingTime" },
        }, CancellationToken.None);

    /// <summary>Sends the reply envelope, THEN acks — a crash between the two still delivers the response.</summary>
    private Task ReplyThenAckAsync(string requestId, string token, Output result, CancellationToken ct)
        => ReplyRawThenAckAsync(requestId, token, HighwayJson.EncodeEnvelope(NodeName, result), ct);

    /// <summary>
    /// As <see cref="ReplyThenAckAsync"/>, but for an envelope that already exists — the
    /// cached response of a suppressed duplicate, which must be returned byte-for-byte
    /// rather than re-encoded.
    /// </summary>
    private async Task ReplyRawThenAckAsync(string requestId, string token, byte[] responseEnvelope, CancellationToken ct)
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
            await Connection.AckAsync(token, ct).ConfigureAwait(false);
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
