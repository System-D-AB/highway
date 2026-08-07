using System.Text.Json;
using Highway.Abstractions;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// Claims and processes queued work for one queue (feature 014).
///
/// <para>The RPC worker loop without the reply: claim, deserialize, process, acknowledge.
/// Multiple instances of the application run this concurrently against the same queue and
/// <b>compete</b> — that is what makes a queue a queue.</para>
///
/// <para><b>A handler that throws is not acknowledged.</b> The message stays in the
/// processing list, its lease expires, and it is redelivered — which is the whole meaning
/// of at-least-once. Swallowing the exception and acknowledging would silently discard
/// work the sender believes is being done.</para>
/// </summary>
internal sealed class QueueWorkerLoop
{
    private readonly QueueDescriptor _descriptor;
    private readonly IHighwayConnection _connection;
    private readonly ServiceExecutor _executor;
    private readonly string _nodeName;
    private readonly int _concurrency;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate;
    private readonly LoopWake _wake;
    private readonly List<Task> _inflight = [];
    private readonly TimeSpan? _idempotencyWindow;

    public QueueWorkerLoop(
        QueueDescriptor descriptor,
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

        var idempotent = descriptor.MessageType
            .GetCustomAttributes(typeof(IdempotentAttribute), inherit: false)
            .FirstOrDefault() as IdempotentAttribute;

        _idempotencyWindow = idempotent is null
            ? null
            : idempotent.Window ?? RpcWorkerLoop.DefaultIdempotencyWindow;
    }

    public string QueueName => _descriptor.Name;
    public LoopWake Wake => _wake;

    /// <summary>
    /// Two-token contract, as the RPC loop: <paramref name="stopToken"/> stops claiming new
    /// work; <paramref name="workToken"/> governs in-flight processing and is cancelled
    /// only after the drain timeout, so shutdown drains rather than aborts.
    /// </summary>
    public async Task RunAsync(TimeSpan selfHealTimeout, CancellationToken stopToken, CancellationToken workToken)
    {
        _logger.LogInformation("Queue worker started for '{Queue}' (concurrency {Concurrency})",
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
                _logger.LogError(ex, "Queue worker for '{Queue}' hit an unexpected error during drain; continuing",
                    _descriptor.Name);
            }
        }

        Task[] snapshot;
        lock (_inflight) snapshot = [.. _inflight];
        if (snapshot.Length > 0)
            await Task.WhenAll(snapshot).ConfigureAwait(false);

        _logger.LogInformation("Queue worker stopped for '{Queue}'", _descriptor.Name);
    }

    private async Task DrainAsync(CancellationToken stopToken, CancellationToken workToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(stopToken).ConfigureAwait(false);

            (string MessageId, byte[] Payload)? claimed;
            try
            {
                claimed = await _connection.QClaimAsync(_descriptor.Name, _nodeName, stopToken).ConfigureAwait(false);
            }
            catch
            {
                _gate.Release();
                throw;
            }

            if (claimed is null)
            {
                _gate.Release();
                return; // queue empty
            }

            var task = ProcessClaimedAsync(claimed.Value.MessageId, claimed.Value.Payload, workToken);
            lock (_inflight) _inflight.Add(task);

            _ = task.ContinueWith(t =>
            {
                lock (_inflight) _inflight.Remove(t);
            }, TaskScheduler.Default);
        }
    }

    private async Task ProcessClaimedAsync(string messageId, byte[] payload, CancellationToken ct)
    {
        try
        {
            await ProcessAsync(messageId, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-message; lease recovery redelivers it.
        }
        catch (Exception ex)
        {
            // Deliberately not acknowledged: the message returns after its lease expires,
            // and eventually dead-letters if it can never succeed.
            _logger.LogError(ex, "Processing '{MessageId}' on queue '{Queue}' failed; it will be redelivered",
                messageId, _descriptor.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessAsync(string messageId, byte[] payload, CancellationToken ct)
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
            _logger.LogError(ex,
                "Message '{MessageId}' on queue '{Queue}' could not be deserialized; leaving it to dead-letter",
                messageId, _descriptor.Name);
            return;
        }

        if (message is null)
        {
            _logger.LogError("Message '{MessageId}' on queue '{Queue}' deserialized to null; leaving it to dead-letter",
                messageId, _descriptor.Name);
            return;
        }

        if (_idempotencyWindow is { } window)
        {
            var claim = await _connection
                .ClaimIdempotencyAsync(_descriptor.Name, messageId, window, ct).ConfigureAwait(false);

            switch (claim.Outcome)
            {
                case IdempotencyOutcome.Duplicate:
                    _logger.LogDebug("Suppressed a duplicate delivery of '{MessageId}' on queue '{Queue}'",
                        messageId, _descriptor.Name);
                    await _connection.QAckAsync(_descriptor.Name, _nodeName, messageId, ct).ConfigureAwait(false);
                    return;

                case IdempotencyOutcome.InProgress:
                    // Another attempt holds the claim, or held it when its process died.
                    // Neither run nor acknowledge: re-running on a stale marker would break
                    // the only promise [Idempotent] makes.
                    _logger.LogDebug("Delivery of '{MessageId}' on queue '{Queue}' is already in progress",
                        messageId, _descriptor.Name);
                    return;
            }
        }

        await _executor.ExecuteProcessorAsync(_descriptor.Name, message, ct).ConfigureAwait(false);

        if (_idempotencyWindow is { } completeWindow)
        {
            // A queue has no response to cache, so the marker simply records completion.
            await _connection
                .CompleteIdempotencyAsync(_descriptor.Name, messageId, [1], completeWindow, ct)
                .ConfigureAwait(false);
        }

        await _connection.QAckAsync(_descriptor.Name, _nodeName, messageId, ct).ConfigureAwait(false);
    }
}
