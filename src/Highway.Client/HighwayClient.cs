using System.Text.Json;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Observability;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Highway.Client;

/// <summary>
/// The Highway client. Sends RPC and Pub/Sub requests through the Highway server.
/// All calls go through the server — there is no local dispatch.
///
/// <para><c>ExecuteAsync</c> never throws for service-level outcomes — failures
/// are data (status codes on the response). The one intentional exception path
/// is caller cancellation (<see cref="OperationCanceledException"/>).
/// <c>PublishAsync</c> has no response object, so its failures are the documented
/// exceptions.</para>
/// </summary>
internal sealed class HighwayClient : IHighwayClient
{
    private readonly ICatalog _catalog;
    private readonly HighwayOptions _options;
    private readonly IHighwayEngine _engine;
    private readonly IHighwayEngineInternals _engineInternals;
    private readonly ILogger<HighwayClient> _logger;

    public HighwayClient(
        ICatalog catalog,
        HighwayOptions options,
        IHighwayEngine engine,
        IHighwayEngineInternals engineInternals,
        ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog;
        _options = options;
        _engine = engine;
        _engineInternals = engineInternals;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<HighwayClient>();
    }

    public Task<TResponse> ExecuteAsync<TResponse>(IReturn<TResponse> request, CancellationToken ct = default)
        where TResponse : Output
        => ExecuteCoreAsync(request, perCallTimeout: null, ct);

    public Task<TResponse> ExecuteAsync<TResponse>(IReturn<TResponse> request, TimeSpan timeout, CancellationToken ct = default)
        where TResponse : Output
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return ExecuteCoreAsync(request, timeout, ct);
    }

    private async Task<TResponse> ExecuteCoreAsync<TResponse>(IReturn<TResponse> request, TimeSpan? perCallTimeout, CancellationToken ct)
        where TResponse : Output
    {
        var responseType = typeof(TResponse);

        if (_engine.State != EngineState.Running
            || _engineInternals.Connection is not { } connection
            || _engineInternals.PendingCalls is not { } pending)
        {
            return Cast<TResponse>(PendingCallRegistry.BuildErrorResponse(
                responseType,
                StatusCodes.Status503ServiceUnavailable,
                "SERVER_UNAVAILABLE",
                "The Highway engine is not running. Start the engine (hosted service or IHighwayEngine.StartAsync) before calling services."));
        }

        // Local-only catalog lookup — an unknown request type never touches the network.
        var serviceName = _catalog.GetServiceNameForRequestType(request.GetType());
        if (serviceName is null)
        {
            return Cast<TResponse>(PendingCallRegistry.BuildErrorResponse(
                responseType,
                StatusCodes.Status404NotFound,
                "SERVICE_NOT_FOUND",
                $"The request type '{request.GetType().FullName}' is not registered in this node's catalog."));
        }

        // Optional fast-fail (006): ask the registry whether anyone hosts this
        // service before enqueuing, so a misconfiguration surfaces in
        // milliseconds instead of after CallTimeout. Only a fresh, successful,
        // empty discovery result gets here — a failed or stale lookup returns
        // Proceed, so this can never drop a request that would be served.
        if (_options.FastFailEnabled && _engineInternals.Discovery is { } discovery)
        {
            var outcome = await discovery.CheckAsync(serviceName, ct).ConfigureAwait(false);
            if (outcome == DiscoveryOutcome.NoLiveHosts)
            {
                return Cast<TResponse>(PendingCallRegistry.BuildErrorResponse(
                    responseType,
                    StatusCodes.Status404NotFound,
                    "SERVICE_NOT_FOUND",
                    $"No live node currently hosts the service '{serviceName}'."));
            }
        }

        // 074: how long this caller waits — the call's own timeout, else the contract's, else CallTimeout — capped by
        // the time left on the call whose handler is making this one (D6): an inner call never outlives its caller.
        // D1: the timeout goes on the wire only when this call or its contract sets one (or a handler's deadline caps
        // it); otherwise the broker's DefaultRpcTimeout applies — the same 30 s as CallTimeout by default.
        var declared = perCallTimeout ?? ServiceTimeouts.For(request.GetType());
        var timeout = declared ?? _options.CallTimeout;
        var sendTimeout = declared is not null;
        // Never wait longer than the broker will run the request (it clamps to its MaxRpcTimeout, learned at start).
        if (timeout > _options.LearnedMaxRpcTimeout) { timeout = _options.LearnedMaxRpcTimeout; sendTimeout = true; }
        if (AmbientDeadline.Remaining is { } left)
        {
            if (left <= TimeSpan.Zero)
            {
                return Cast<TResponse>(PendingCallRegistry.BuildErrorResponse(
                    responseType,
                    StatusCodes.Status504GatewayTimeout,
                    "DEADLINE_PASSED",
                    $"The call to '{serviceName}' was not made: the request this handler is serving has already reached its deadline."));
            }
            if (left < timeout) { timeout = left; sendTimeout = true; }
        }

        var requestIdForSpan = Guid.NewGuid().ToString("N");

        // Span first, so the traceparent it establishes rides the envelope and
        // the server-side span joins this trace. Null when nothing is listening.
        using var activity = _options.ActivitiesEnabled
            ? HighwayActivity.StartCall(serviceName, requestIdForSpan, _options.NodeName)
            : null;

        byte[] envelope;
        try
        {
            envelope = HighwayJson.EncodeEnvelope(
                _options.NodeName, request,
                _options.ActivitiesEnabled ? HighwayActivity.CurrentTraceParent() : null);
        }
        catch (JsonException ex)
        {
            return Cast<TResponse>(PendingCallRegistry.BuildErrorResponse(
                responseType,
                StatusCodes.Status400BadRequest,
                "BAD_REQUEST",
                $"The request could not be serialized: {ex.Message}"));
        }

        if (envelope.Length > _options.EffectiveMaxPayloadBytes)
        {
            return Cast<TResponse>(PendingCallRegistry.BuildErrorResponse(
                responseType,
                StatusCodes.Status413PayloadTooLarge,
                "PAYLOAD_TOO_LARGE",
                $"The request envelope is {envelope.Length} bytes, exceeding the maximum of {_options.EffectiveMaxPayloadBytes} bytes. Raise MaxPayloadBytes on the server (and client) to send larger messages."));
        }

        var requestId = requestIdForSpan;
        var responseTask = pending.Register(requestId, responseType, timeout, ct,
            serviceName, envelope);   // replay data: re-driven same-id after a failover (042-1b)

        var rpcStart = System.Diagnostics.Stopwatch.GetTimestamp();   // 051 R2: caller-side RPC latency
        try
        {
            try
            {
                await connection.CallAsync(serviceName, requestId, envelope, sendTimeout ? timeout : null, ct).ConfigureAwait(false);
            }
            finally
            {
                pending.MarkSent(requestId);   // 074: a withdrawal waits for this send
            }
        }
        catch (HighwayTransportException ex) when (ex.Message.Contains("HW_NO_WORKERS", StringComparison.Ordinal))
        {
            // 074 (D4): no node has ever served this service — a typo or a service not deployed. Answered at once,
            // so the caller can fall back instead of waiting out its timeout.
            var failure = PendingCallRegistry.BuildErrorResponse(
                responseType,
                StatusCodes.Status503ServiceUnavailable,
                "NO_WORKERS",
                $"No node has ever served '{serviceName}'.");
            pending.TryFail(requestId, failure);
            HighwayClientMetrics.RecordRpc(ok: false, System.Diagnostics.Stopwatch.GetElapsedTime(rpcStart).TotalSeconds);
            return Cast<TResponse>(failure);
        }
        catch (HighwayTransportException ex)
        {
            // Permanent send failure — complete now with 503 instead of letting
            // the call wait out its timeout. (Transient aborts are already retried
            // with bounded backoff inside the connection.)
            _logger.LogError(ex, "HW.CALL failed permanently for service '{Service}' (request {RequestId})",
                serviceName, requestId);
            var failure = PendingCallRegistry.BuildErrorResponse(
                responseType,
                StatusCodes.Status503ServiceUnavailable,
                "SERVER_UNAVAILABLE",
                $"The server rejected or could not receive the call: {ex.Message}");
            pending.TryFail(requestId, failure);
            HighwayClientMetrics.RecordRpc(ok: false, System.Diagnostics.Stopwatch.GetElapsedTime(rpcStart).TotalSeconds);
            return Cast<TResponse>(failure);
        }
        // OperationCanceledException propagates: the registry's linked token also
        // cancels the pending entry, so nothing leaks.

        var response = await responseTask.ConfigureAwait(false);
        HighwayActivity.SetOutcome(activity, response.StatusCode, response.Error?.Code);
        HighwayClientMetrics.RecordRpc(response.StatusCode < 400, System.Diagnostics.Stopwatch.GetElapsedTime(rpcStart).TotalSeconds);
        return Cast<TResponse>(response);
    }

    public Task<string> SendAsync(ISend message, CancellationToken ct = default)
        => SendCoreAsync(message, deliverAt: null, ct);

    /// <inheritdoc/>
    public Task<string> SendAsync(ISend message, TimeSpan delay, CancellationToken ct = default)
        => SendCoreAsync(
            message,
            // A non-positive delay sends immediately rather than failing: it falls out of
            // ordinary arithmetic on a caller's schedule, and refusing it would make every
            // caller write the guard Highway can write once.
            delay > TimeSpan.Zero ? DateTimeOffset.UtcNow + delay : null,
            ct);

    private async Task<string> SendCoreAsync(ISend message, DateTimeOffset? deliverAt, CancellationToken ct)
    {
        if (_engine.State != EngineState.Running || _engineInternals.Connection is not { } connection)
        {
            throw new HighwayTransportException(
                "The Highway engine is not running. Start the engine before sending.");
        }

        var queueName = _catalog.GetQueueNameForMessageType(message.GetType())
            ?? throw new QueueNotRegisteredException(message.GetType());

        byte[] envelope;
        try
        {
            envelope = HighwayJson.EncodeEnvelope(
                _options.NodeName, message,
                _options.ActivitiesEnabled ? HighwayActivity.CurrentTraceParent() : null);
        }
        catch (JsonException ex)
        {
            throw new HighwayTransportException($"The message could not be serialized: {ex.Message}");
        }

        if (envelope.Length > _options.EffectiveMaxPayloadBytes)
            throw new PayloadTooLargeException(envelope.Length, _options.EffectiveMaxPayloadBytes);

        var messageId = Guid.NewGuid().ToString("N");
        await connection.QSendAsync(queueName, messageId, envelope, deliverAt, ct).ConfigureAwait(false);

        _logger.LogDebug("Sent to queue '{Queue}' as {MessageId}", queueName, messageId);
        return messageId;
    }

    public Task PublishAsync(IPublish message, CancellationToken ct = default)
        => PublishCoreAsync(message, deliverAt: null, ct);

    /// <inheritdoc/>
    public Task PublishAsync(IPublish message, TimeSpan delay, CancellationToken ct = default)
        => PublishCoreAsync(
            message,
            // A non-positive delay is an immediate publish rather than an error: it falls
            // out of ordinary arithmetic on a caller's schedule, and refusing it would
            // make every caller write the guard Highway can write once.
            delay > TimeSpan.Zero ? DateTimeOffset.UtcNow + delay : null,
            ct);

    private async Task PublishCoreAsync(IPublish message, DateTimeOffset? deliverAt, CancellationToken ct)
    {
        if (_engine.State != EngineState.Running || _engineInternals.Connection is not { } connection)
        {
            throw new HighwayTransportException(
                "The Highway engine is not running. Start the engine before publishing.");
        }

        var channelName = _catalog.GetChannelNameForMessageType(message.GetType());
        if (channelName is null)
            throw new ChannelNotRegisteredException(message.GetType());

        using var activity = _options.ActivitiesEnabled
            ? HighwayActivity.StartPublish(channelName, _options.NodeName)
            : null;

        byte[] envelope;
        try
        {
            envelope = HighwayJson.EncodeEnvelope(
                _options.NodeName, message,
                _options.ActivitiesEnabled ? HighwayActivity.CurrentTraceParent() : null);
        }
        catch (JsonException ex)
        {
            throw new HighwayTransportException($"The message could not be serialized: {ex.Message}");
        }

        if (envelope.Length > _options.EffectiveMaxPayloadBytes)
            throw new PayloadTooLargeException(envelope.Length, _options.EffectiveMaxPayloadBytes);

        // The connection retries the transient class (watch-conflicted publishes
        // delivered nothing) with bounded backoff before this throws.
        var groupCount = await connection
            .PublishCommandAsync(channelName, envelope, deliverAt, ct).ConfigureAwait(false);

        if (deliverAt is { } at)
        {
            _logger.LogDebug(
                "Published to channel '{Channel}' for delivery no earlier than {DeliverAt:O}; " +
                "held until a consumer polls after that time",
                channelName, at);
        }
        else
        {
            _logger.LogDebug("Published to channel '{Channel}'; delivered to {GroupCount} groups",
                channelName, groupCount);
        }
    }

    private static TResponse Cast<TResponse>(Output response) where TResponse : Output
        => (TResponse)response;
}
