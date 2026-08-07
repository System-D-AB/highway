using System.Text.Json;
using Highway.Abstractions;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// One loop per catalog service. Waits for a wake (doorbell, backstop tick, or
/// stop), then drains <c>HW.DEQUEUE</c> to nil; each dequeued request is processed
/// under a concurrency gate: parse envelope → run the service via
/// <see cref="ServiceExecutor"/> → <c>HW.REPLY</c> BEFORE <c>HW.ACK</c>.
///
/// <para>Retry policy (004.1): the connection already retries the transient
/// class with bounded backoff. A surfaced permanent error is logged and the
/// item dropped — never retried, so the loop cannot spin on poisoned input.
/// The loop itself never dies.</para>
/// </summary>
internal sealed class RpcWorkerLoop
{
    private readonly ServiceDescriptor _descriptor;
    private readonly IHighwayConnection _connection;
    private readonly ServiceExecutor _executor;
    private readonly string _nodeName;
    private readonly int _concurrency;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate;
    private readonly LoopWake _wake;
    private readonly List<Task> _inflight = [];

    /// <summary>
    /// Deduplication window for this service's request contract, or <see langword="null"/>
    /// when the contract carries no <c>[Idempotent]</c> attribute — in which case every
    /// delivery takes exactly the path it always did.
    /// </summary>
    private readonly TimeSpan? _idempotencyWindow;

    public RpcWorkerLoop(
        ServiceDescriptor descriptor,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string nodeName,
        int concurrency,
        LoopWake wake,
        ILogger logger)
    {
        _descriptor = descriptor;
        _connection = connection;
        _executor = executor;
        _nodeName = nodeName;
        _concurrency = Math.Max(1, concurrency);
        _wake = wake;
        _logger = logger;
        _gate = new SemaphoreSlim(_concurrency, _concurrency);

        var idempotent = descriptor.RequestType
            .GetCustomAttributes(typeof(IdempotentAttribute), inherit: false)
            .FirstOrDefault() as IdempotentAttribute;

        _idempotencyWindow = idempotent is null
            ? null
            : idempotent.Window ?? DefaultIdempotencyWindow;
    }

    /// <summary>
    /// Window used when <c>[Idempotent]</c> names none. Matches the server's default
    /// <c>ReplySlotTtl</c>: a response nobody can collect any more is a response there is
    /// no point deduplicating against.
    /// </summary>
    internal static readonly TimeSpan DefaultIdempotencyWindow = TimeSpan.FromMinutes(5);

    public string ServiceName => _descriptor.Name;
    public LoopWake Wake => _wake;

    /// <summary>
    /// Two-token contract: <paramref name="stopToken"/> stops dequeuing new work;
    /// <paramref name="workToken"/> governs in-flight processing and is only
    /// cancelled after the drain timeout — so graceful shutdown drains rather
    /// than aborts.
    /// </summary>
    public async Task RunAsync(TimeSpan selfHealTimeout, CancellationToken stopToken, CancellationToken workToken)
    {
        _logger.LogInformation("Worker loop started for service '{Service}' (concurrency {Concurrency})",
            _descriptor.Name, _concurrency);

        while (!stopToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(selfHealTimeout, stopToken).ConfigureAwait(false);
            if (stopToken.IsCancellationRequested) break;

            try
            {
                await DrainAsync(stopToken, workToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop for '{Service}' hit an unexpected error during drain; continuing",
                    _descriptor.Name);
            }
        }

        // Keep the loop "active" until every spawned processing task finishes —
        // this is what the engine's drain wait observes.
        Task[] snapshot;
        lock (_inflight) snapshot = [.. _inflight];
        try
        {
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
        catch
        {
            // Individual tasks log their own failures.
        }

        _logger.LogInformation("Worker loop stopped for service '{Service}'", _descriptor.Name);
    }

    private async Task DrainAsync(CancellationToken stopToken, CancellationToken workToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            (string RequestId, byte[] Payload)? item;
            try
            {
                item = await _connection.DequeueAsync(_descriptor.Name, _nodeName, stopToken).ConfigureAwait(false);
            }
            catch (HighwayTransientException ex)
            {
                // Bounded retries already exhausted in the connection. Back off, then
                // let the next wake retry. This is the retryable class only.
                _logger.LogWarning(ex, "Transient abort dequeuing '{Service}'; will retry on next wake", _descriptor.Name);
                await Task.Delay(100, stopToken).ConfigureAwait(false);
                return;
            }
            catch (HighwayTransportException ex)
            {
                // Permanent failure: log and drop this drain pass. Never retry in a tight loop.
                _logger.LogError(ex, "Permanent error dequeuing '{Service}'; ending drain pass", _descriptor.Name);
                return;
            }

            if (item is null)
                return; // queue drained to nil

            var (requestId, payload) = item.Value;

            // Bound in-flight processing; process on the thread pool so a
            // synchronous-heavy handler cannot stall the drain.
            await _gate.WaitAsync(stopToken).ConfigureAwait(false);
            var task = Task.Run(() => ProcessAndReleaseAsync(requestId, payload, workToken), CancellationToken.None);
            lock (_inflight)
            {
                _inflight.Add(task);
                if (_inflight.Count > 64)
                    _inflight.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    private async Task ProcessAndReleaseAsync(string requestId, byte[] payload, CancellationToken ct)
    {
        try
        {
            await ProcessAsync(requestId, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine stopping mid-request; server lease recovery handles redelivery.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing request '{RequestId}' for '{Service}'",
                requestId, _descriptor.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessAsync(string requestId, byte[] payload, CancellationToken ct)
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
            _logger.LogWarning(ex, "Poison envelope for request '{RequestId}' on '{Service}'; replying 400 and acking",
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
        if (_idempotencyWindow is { } window)
        {
            var claim = await _connection
                .ClaimIdempotencyAsync(_descriptor.Name, requestId, window, ct).ConfigureAwait(false);

            switch (claim.Outcome)
            {
                case IdempotencyOutcome.Duplicate:
                    // The handler already ran for this delivery. Reply with what it
                    // produced, so the caller is unaffected by the duplication, and ack so
                    // the redelivery stops.
                    _logger.LogDebug(
                        "Suppressed a duplicate delivery of '{RequestId}' on '{Service}'; replying with the original response",
                        requestId, _descriptor.Name);
                    await ReplyRawThenAckAsync(requestId, claim.Response!, ct).ConfigureAwait(false);
                    return;

                case IdempotencyOutcome.InProgress:
                    // Another attempt holds the claim — running now, or crashed while
                    // running. Neither run nor reply nor ack: the lease expires and the
                    // request is redelivered after the window. Re-running on a stale
                    // marker would break the only promise [Idempotent] makes.
                    _logger.LogDebug(
                        "Delivery of '{RequestId}' on '{Service}' is already in progress; leaving it for lease recovery",
                        requestId, _descriptor.Name);
                    return;
            }
        }

        var raw = await _executor.ExecuteServiceAsync(_descriptor.Name, request, ct).ConfigureAwait(false);

        result = raw as Output ?? new GenericOutput
        {
            StatusCode = StatusCodes.Status500InternalServerError,
            Error = new ErrorDetail { Code = "INTERNAL_ERROR", Message = "The service returned a non-Output result." },
        };

        var envelopeBytes = HighwayJson.EncodeEnvelope(_nodeName, result);

        if (_idempotencyWindow is { } completeWindow)
        {
            // Replace the in-progress marker with the response before replying, so a
            // redelivery that arrives during the reply finds an answer rather than a
            // marker it must wait out.
            await _connection
                .CompleteIdempotencyAsync(_descriptor.Name, requestId, envelopeBytes, completeWindow, ct)
                .ConfigureAwait(false);
        }

        await ReplyRawThenAckAsync(requestId, envelopeBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the reply envelope, THEN acks — a crash between the two still delivers the response.</summary>
    private Task ReplyThenAckAsync(string requestId, Output result, CancellationToken ct)
        => ReplyRawThenAckAsync(requestId, HighwayJson.EncodeEnvelope(_nodeName, result), ct);

    /// <summary>
    /// As <see cref="ReplyThenAckAsync"/>, but for an envelope that already exists — the
    /// cached response of a suppressed duplicate, which must be returned byte-for-byte
    /// rather than re-encoded.
    /// </summary>
    private async Task ReplyRawThenAckAsync(string requestId, byte[] responseEnvelope, CancellationToken ct)
    {
        try
        {
            await _connection.ReplyAsync(requestId, responseEnvelope, ct).ConfigureAwait(false);
        }
        catch (HighwayTransportException ex)
        {
            _logger.LogError(ex, "Permanent error replying to '{RequestId}' on '{Service}'; not acking",
                requestId, _descriptor.Name);
            return; // do not ack — lease recovery will redeliver
        }

        try
        {
            await _connection.AckAsync(_descriptor.Name, _nodeName, requestId, ct).ConfigureAwait(false);
        }
        catch (HighwayTransportException ex)
        {
            // Reply already delivered; a failed ack means possible duplicate execution
            // on redelivery (at-least-once). Log loudly.
            _logger.LogError(ex, "Reply sent but ACK failed for '{RequestId}' on '{Service}' (at-least-once duplicate risk)",
                requestId, _descriptor.Name);
        }
    }
}
