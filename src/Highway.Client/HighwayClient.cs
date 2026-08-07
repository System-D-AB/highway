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
    /// <summary>
    /// Client-side mirror of the server's MaxPayloadBytes default (004). The
    /// server remains the authority — a mismatch surfaces as HW_PAYLOAD_TOO_LARGE.
    /// </summary>
    internal const int MaxPayloadBytes = 1 * 1024 * 1024;

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

    public async Task<TResponse> ExecuteAsync<TResponse>(IReturn<TResponse> request, CancellationToken ct = default)
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

        if (envelope.Length > MaxPayloadBytes)
        {
            return Cast<TResponse>(PendingCallRegistry.BuildErrorResponse(
                responseType,
                StatusCodes.Status413PayloadTooLarge,
                "PAYLOAD_TOO_LARGE",
                $"The request envelope is {envelope.Length} bytes, exceeding the maximum of {MaxPayloadBytes} bytes."));
        }

        var requestId = requestIdForSpan;
        var responseTask = pending.Register(requestId, responseType, _options.CallTimeout, ct);

        try
        {
            await connection.CallAsync(serviceName, requestId, envelope, ct).ConfigureAwait(false);
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
            return Cast<TResponse>(failure);
        }
        // OperationCanceledException propagates: the registry's linked token also
        // cancels the pending entry, so nothing leaks.

        var response = await responseTask.ConfigureAwait(false);
        HighwayActivity.SetOutcome(activity, response.StatusCode, response.Error?.Code);
        return Cast<TResponse>(response);
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

        if (envelope.Length > MaxPayloadBytes)
            throw new PayloadTooLargeException(envelope.Length, MaxPayloadBytes);

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
