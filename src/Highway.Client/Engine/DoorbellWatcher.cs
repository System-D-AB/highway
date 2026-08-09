using Highway.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Highway.Client.Engine;

/// <summary>
/// Owns all doorbell subscriptions (<c>hw:door:*</c>) and routes wakes.
/// Doorbells are a pure latency optimization — correctness rides on the
/// backstop sweep. With <c>DoorbellsEnabled == false</c> nothing is
/// subscribed and the engine still works at backstop-interval latency.
///
/// <para>SE.Redis re-establishes subscriptions automatically after a
/// reconnect (verified by ClientReconnectTests), so no re-issue logic
/// is needed here.</para>
/// </summary>
internal sealed class DoorbellWatcher
{
    private readonly IHighwayConnection _connection;
    private readonly PendingCallRegistry _registry;
    private readonly bool _enabled;
    private readonly ILogger<DoorbellWatcher> _logger;

    private readonly Dictionary<string, LoopWake> _serviceWakes = new();

    public DoorbellWatcher(
        IHighwayConnection connection,
        PendingCallRegistry registry,
        bool doorbellsEnabled,
        ILoggerFactory? loggerFactory = null)
    {
        _connection = connection;
        _registry = registry;
        _enabled = doorbellsEnabled;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DoorbellWatcher>();
    }

    /// <summary>Registers the wake for one catalog service (call before StartAsync).</summary>
    public void RegisterServiceWake(string service, LoopWake wake) => _serviceWakes[service] = wake;

    /// <summary>
    /// Subscribes the reply doorbell (one per node, shared by all pending calls)
    /// and every service doorbell.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Doorbells disabled — engine runs on the backstop sweep only");
            return;
        }

        await _connection.SubscribeDoorbellAsync("hw:door:rep", OnReplyDoorbell, ct).ConfigureAwait(false);

        foreach (var service in _serviceWakes.Keys)
        {
            var captured = _serviceWakes[service];
            await _connection.SubscribeDoorbellAsync($"hw:door:svc:{service}", _ => captured.Signal(), ct)
                .ConfigureAwait(false);
        }
    }

    private void OnReplyDoorbell(string requestId)
    {
        // Minimal work in the handler; the GET/DEL happens in the registry.
        _ = _registry.TryCompleteFromSlotAsync(requestId);
    }
}
