using System.Text.Json;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// Keeps this node present in the server's registry (feature 006).
///
/// <para>Registers <b>once</b> at start, then proves liveness on an interval.
/// The catalog is static for a node's lifetime, so re-sending it every beat
/// would put the whole catalog on the wire per node per interval and force a
/// server-side parse to rebuild an index that never changes. A steady-state beat
/// is a few bytes regardless of how many services the node hosts.</para>
///
/// <para>When the server replies <c>REGISTER</c> it holds no record for this
/// node — pruned, or lost with a restart. The loop re-registers immediately
/// rather than waiting for the next tick, because until it does the node is
/// alive but absent from discovery.</para>
///
/// <para>Heartbeat failure degrades discovery only. RPC and pub/sub keep working
/// throughout: a node that cannot beat is invisible, not broken, and failing the
/// engine here would turn an observability outage into a total one.</para>
/// </summary>
internal sealed class HeartbeatLoop
{
    private readonly IHighwayConnection _connection;
    private readonly string _nodeName;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly byte[] _catalogJson;

    public HeartbeatLoop(
        IHighwayConnection connection,
        ICatalog catalog,
        string nodeName,
        TimeSpan interval,
        ILogger logger)
    {
        _connection = connection;
        _nodeName = nodeName;
        _interval = interval;
        _logger = logger;

        // Serialized exactly once: the catalog cannot change for this node.
        _catalogJson = JsonSerializer.SerializeToUtf8Bytes(
            catalog.ToCatalogInfo(), HighwayJson.SerializerOptions);
    }

    /// <summary>Catalog payload size, for diagnostics and tests.</summary>
    public int CatalogBytes => _catalogJson.Length;

    /// <summary>
    /// Sends the registration form. The engine awaits this during startup, so a
    /// node is discoverable by the time it reports Running.
    ///
    /// <para>Doing it here rather than as the loop's first action matters: if
    /// registration were merely the first thing a background task did, a caller
    /// could complete <c>StartAsync</c> and issue its first request before the
    /// node existed in the registry — and with fast-fail on, that first request
    /// after a deployment would 404 spuriously.</para>
    ///
    /// <para>Never throws: a node that cannot register is invisible, not broken.</para>
    /// </summary>
    public Task RegisterAsync(CancellationToken ct = default) => TryRegisterAsync(ct);

    public async Task RunAsync(CancellationToken stopToken)
    {
        _logger.LogInformation(
            "Heartbeat starting for node '{Node}' (interval {Interval}, catalog {Bytes} bytes)",
            _nodeName, _interval, _catalogJson.Length);

        while (!stopToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var reply = await _connection.HeartbeatAsync(_nodeName, stopToken).ConfigureAwait(false);
                if (reply == HeartbeatReply.ReRegisterRequired)
                {
                    _logger.LogInformation(
                        "Server holds no registration for '{Node}' — re-registering", _nodeName);
                    await TryRegisterAsync(stopToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HighwayTransientException ex)
            {
                _logger.LogDebug(ex, "Transient abort on heartbeat for '{Node}'; next beat retries", _nodeName);
            }
            catch (HighwayTransportException ex)
            {
                _logger.LogError(ex, "Heartbeat failed for '{Node}'; node may be treated as stale", _nodeName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected heartbeat error for '{Node}'; continuing", _nodeName);
            }
        }

        _logger.LogInformation("Heartbeat stopped for node '{Node}'", _nodeName);
    }

    /// <summary>
    /// Announces departure so operators see the node leave now rather than after
    /// the expiry window. Best effort: shutdown never blocks or fails on it, and
    /// a node that is killed instead simply expires on the normal timeline.
    /// </summary>
    public async Task DepartAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _connection.DepartAsync(_nodeName, cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Node '{Node}' announced departure", _nodeName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Departure announcement failed for '{Node}'; it will expire normally", _nodeName);
        }
    }

    private async Task TryRegisterAsync(CancellationToken ct)
    {
        try
        {
            await _connection.RegisterAsync(_nodeName, _catalogJson, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Registration failed for '{Node}'; the next beat will retry via the re-register signal", _nodeName);
        }
    }
}
