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
    private readonly Dictionary<string, LoopWake> _groupWakes = new();

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

    /// <summary>Registers the wake for one catalog channel group (call before StartAsync).</summary>
    public void RegisterGroupWake(string channel, string group, LoopWake wake)
        => _groupWakes[GroupDoorbellKey(channel, group)] = wake;

    /// <summary>
    /// Subscribes the reply doorbell (one per node, shared by all pending calls),
    /// every service doorbell, and every group doorbell.
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

        foreach (var key in _groupWakes.Keys)
        {
            var captured = _groupWakes[key];
            await _connection.SubscribeDoorbellAsync(key, _ => captured.Signal(), ct).ConfigureAwait(false);
        }
    }

    private void OnReplyDoorbell(string requestId)
    {
        // Minimal work in the handler; the GET/DEL happens in the registry.
        _ = _registry.TryCompleteFromSlotAsync(requestId);
    }

    private static string GroupDoorbellKey(string channel, string group)
        => $"hw:door:ch:{channel}:grp:{group}";
}
