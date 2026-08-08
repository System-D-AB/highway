using System.Text.Json;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// One loop per catalog channel that has local subscribers. Waits for a wake
/// (group doorbell, backstop tick, or stop), then drains <c>HW.RECEIVE</c> in
/// batches; each message is dispatched to all local subscribers and only then
/// acknowledged with <c>HW.RACK</c> — a crash mid-dispatch causes redelivery,
/// not loss.
///
/// <para>Poison messages (unparseable envelope/body) are logged and RACKed so
/// they never block the group queue. Subscriber failures are swallowed by the
/// executor and never prevent siblings or the ack (v0.8-compatible semantics).
/// Retry policy mirrors <see cref="RpcWorkerLoop"/> (004.1 classification).</para>
/// </summary>
internal sealed class ChannelConsumerLoop
{
    private readonly ChannelDescriptor _descriptor;
    private readonly IHighwayConnection _connection;
    private readonly ServiceExecutor _executor;
    private readonly string _groupName;
    private readonly int _batchSize;
    private readonly ILogger _logger;
    private readonly LoopWake _wake;
    private readonly FailureReporter _reporter;

    public ChannelConsumerLoop(
        ChannelDescriptor descriptor,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string groupName,
        int batchSize,
        LoopWake wake,
        ILogger logger)
    {
        _descriptor = descriptor;
        _connection = connection;
        _executor = executor;
        _groupName = groupName;
        _batchSize = batchSize;
        _wake = wake;
        _logger = logger;
        _reporter = new FailureReporter(connection, logger);
    }

    public string ChannelName => _descriptor.Name;
    public LoopWake Wake => _wake;

    /// <summary>
    /// Two-token contract: <paramref name="stopToken"/> stops receiving new
    /// batches; <paramref name="workToken"/> governs in-flight dispatch and is
    /// only cancelled after the drain timeout.
    /// </summary>
    public async Task RunAsync(TimeSpan selfHealTimeout, CancellationToken stopToken, CancellationToken workToken)
    {
        _logger.LogInformation("Consumer loop started for channel '{Channel}' (group '{Group}')",
            _descriptor.Name, _groupName);

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
                _logger.LogError(ex, "Consumer loop for '{Channel}' hit an unexpected error during drain; continuing",
                    _descriptor.Name);
            }
        }

        _logger.LogInformation("Consumer loop stopped for channel '{Channel}'", _descriptor.Name);
    }

    private async Task DrainAsync(CancellationToken stopToken, CancellationToken workToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            IReadOnlyList<(long MessageId, byte[] Payload)> batch;
            try
            {
                batch = await _connection
                    .ReceiveAsync(_descriptor.Name, _groupName, _batchSize, stopToken)
                    .ConfigureAwait(false);
            }
            catch (HighwayTransientException ex)
            {
                _logger.LogWarning(ex, "Transient abort receiving from '{Channel}'; will retry on next wake",
                    _descriptor.Name);
                await Task.Delay(100, stopToken).ConfigureAwait(false);
                return;
            }
            catch (HighwayTransportException ex)
            {
                _logger.LogError(ex, "Permanent error receiving from '{Channel}'; ending drain pass",
                    _descriptor.Name);
                return;
            }

            if (batch.Count == 0)
                return;

            foreach (var (messageId, payload) in batch)
            {
                if (stopToken.IsCancellationRequested) return;
                await DispatchAndAckAsync(messageId, payload, workToken).ConfigureAwait(false);
            }

            if (batch.Count < _batchSize)
                return; // short batch — queue drained
        }
    }

    private async Task DispatchAndAckAsync(long messageId, byte[] payload, CancellationToken ct)
    {
        try
        {
            var envelope = HighwayJson.DecodeEnvelope(payload);
            var message = HighwayJson.DeserializeBody(envelope, _descriptor.MessageType);

            if (message is null)
            {
                _logger.LogWarning("Message {MessageId} on '{Channel}' deserialized to null; acking without dispatch",
                    messageId, _descriptor.Name);
            }
            else
            {
                // Sequential fan-out to all local subscribers; failures are
                // swallowed inside the executor and never block siblings.
                await _executor.ExecuteSubscribersAsync(_descriptor.Name, message, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            // Poison message: log and ack so the group queue never blocks.
            _logger.LogWarning(ex, "Poison message {MessageId} on '{Channel}'; acking without dispatch",
                messageId, _descriptor.Name);
        }
        catch (OperationCanceledException)
        {
            // Engine stopping mid-dispatch; skip the ack so the server redelivers.
            return;
        }
        catch (Exception ex)
        {
            // Routed through the shared reporter (015 T2) even though this loop is otherwise
            // batch-shaped: reporting is the one concern all three loops genuinely share, and
            // a pub/sub failure that nobody can diagnose is the same problem as any other.
            await _reporter.ReportAsync(
                new FailureTarget(FailureFamily.Channel, _descriptor.Name, _groupName),
                messageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ex,
                "it is acknowledged anyway, so the group queue never blocks",
                CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await _connection.RackAsync(_descriptor.Name, _groupName, messageId, ct).ConfigureAwait(false);
        }
        catch (HighwayTransportException ex)
        {
            _logger.LogError(ex, "RACK failed for message {MessageId} on '{Channel}' (at-least-once duplicate risk)",
                messageId, _descriptor.Name);
        }
    }
}
