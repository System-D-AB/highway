using Highway.Client.Execution;
using Highway.Client.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Highway.Client.Engine;

/// <summary>
/// Orchestrates the client-side runtime. Start order: connect (fail fast) →
/// doorbell subscriptions → HW.SUBSCRIBE every catalog channel → worker and
/// consumer loops → backstop sweeper. Stop order: cancel loops → await
/// in-flight work up to <see cref="HighwayOptions.DrainTimeout"/> → dispose
/// the connection. Never sends HW.UNSUBSCRIBE — groups persist across restarts.
/// </summary>
internal sealed class HighwayEngine : IHighwayEngine, IHighwayEngineInternals, IAsyncDisposable
{
    private readonly ICatalog _catalog;
    private readonly HighwayOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HighwayEngine> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private EngineState _state = EngineState.NotStarted;
    private CancellationTokenSource? _stopCts;   // stops loops taking new work
    private CancellationTokenSource? _workCts;   // governs in-flight work; cancelled only after drain timeout
    private HighwayConnection? _connection;
    private PendingCallRegistry? _pendingCalls;
    private ServiceDiscoveryCache? _discovery;
    private HeartbeatLoop? _heartbeat;
    private readonly List<Task> _runningTasks = [];
    private int _activeOperations;
    private bool _disposed;

    public HighwayEngine(
        ICatalog catalog,
        HighwayOptions options,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog;
        _options = options;
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HighwayEngine>();
    }

    public EngineState State => _state;
    IHighwayConnection? IHighwayEngineInternals.Connection => _connection;
    PendingCallRegistry? IHighwayEngineInternals.PendingCalls => _pendingCalls;
    ServiceDiscoveryCache? IHighwayEngineInternals.Discovery => _discovery;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);

        // The double-start guard must sit OUTSIDE the startup try/catch — a
        // rejection here is not a startup failure and must not reset state.
        if (_state is EngineState.Running or EngineState.Draining)
        {
            _lifecycleLock.Release();
            throw new InvalidOperationException(
                $"The Highway engine is already {_state}. Each engine instance starts exactly once.");
        }

        try
        {
            _stopCts = new CancellationTokenSource();
            _workCts = new CancellationTokenSource();
            var stopToken = _stopCts.Token;
            var workToken = _workCts.Token;

            // 1. Connect — fail fast, descriptive error, no silent retry loop.
            _connection = await HighwayConnection.ConnectAsync(_options.Server!, _options, ct).ConfigureAwait(false);
            _pendingCalls = new PendingCallRegistry(_connection);

            var executor = new ServiceExecutor(_catalog, _scopeFactory);
            var watcher = new DoorbellWatcher(_connection, _pendingCalls, _options.DoorbellsEnabled, _loggerFactory);

            // 2. Register wakes, then subscribe doorbells.
            var wakes = new List<LoopWake>();
            var loopTasks = new List<Task>();

            foreach (var service in _catalog.AllServices)
            {
                var wake = new LoopWake();
                watcher.RegisterServiceWake(service.Name, wake);
                var loop = new RpcWorkerLoop(
                    service, _connection, executor, _options.NodeName,
                    _options.WorkerConcurrency, wake,
                    _loggerFactory.CreateLogger($"Highway.Worker.{service.Name}"));
                loopTasks.Add(Task.Run(
                    () => RunTrackedAsync(() => loop.RunAsync(SelfHealTimeout, stopToken, workToken)),
                    CancellationToken.None));
                wakes.Add(wake);
            }

            foreach (var channel in _catalog.AllChannels)
            {
                var wake = new LoopWake();
                watcher.RegisterGroupWake(channel.Name, _options.NodeName, wake);
                var loop = new ChannelConsumerLoop(
                    channel, _connection, executor, _options.NodeName,
                    _options.ReceiveBatchSize, wake,
                    _loggerFactory.CreateLogger($"Highway.Consumer.{channel.Name}"));
                loopTasks.Add(Task.Run(
                    () => RunTrackedAsync(() => loop.RunAsync(SelfHealTimeout, stopToken, workToken)),
                    CancellationToken.None));
                wakes.Add(wake);
            }

            if (_catalog.AllQueues.Count > 0)
                WarnIfQueuesAreNotDurable();

            foreach (var queue in _catalog.AllQueues)
            {
                var wake = new LoopWake();
                var loop = new QueueWorkerLoop(
                    queue, _connection, executor, _options.NodeName,
                    _options.WorkerConcurrency, wake,
                    _loggerFactory.CreateLogger($"Highway.Queue.{queue.Name}"));
                loopTasks.Add(Task.Run(
                    () => RunTrackedAsync(() => loop.RunAsync(SelfHealTimeout, stopToken, workToken)),
                    CancellationToken.None));
                wakes.Add(wake);

                // The doorbell is a latency optimisation; the backstop sweep drives
                // correctness, so a failure to subscribe must not stop the worker.
                if (_options.DoorbellsEnabled)
                {
                    var queueWake = wake;
                    try
                    {
                        await _connection.SubscribeQueueDoorbellAsync(
                            queue.Name, _ => queueWake.Signal(), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Could not subscribe to the doorbell for queue '{Queue}'; the backstop sweep still drives it",
                            queue.Name);
                    }
                }
            }

            await watcher.StartAsync(ct).ConfigureAwait(false);

            // 3. Register this node's subscriber groups (group = NodeName).
            foreach (var channel in _catalog.AllChannels)
            {
                await _connection.SubscribeGroupAsync(channel.Name, _options.NodeName, ct).ConfigureAwait(false);
            }

            // 4. Sweeper last — once everything can be woken.
            var sweeper = new BackstopSweeper(
                _pendingCalls, _options.BackstopInterval, wakes,
                _loggerFactory.CreateLogger<BackstopSweeper>());
            loopTasks.Add(Task.Run(() => sweeper.RunAsync(stopToken), CancellationToken.None));

            // 5. Registry (006): discovery cache for fast-fail, then the heartbeat.
            _discovery = new ServiceDiscoveryCache(_connection, _options.DiscoveryCacheTtl);

            if (_options.HeartbeatEnabled)
            {
                _heartbeat = new HeartbeatLoop(
                    _connection, _catalog, _options.NodeName, _options.HeartbeatInterval,
                    _loggerFactory.CreateLogger<HeartbeatLoop>());

                // Register before reporting Running, so the node is discoverable
                // the moment StartAsync returns. Registering inside the loop
                // instead would let a caller issue its first request before the
                // node existed in the registry.
                await _heartbeat.RegisterAsync(ct).ConfigureAwait(false);

                loopTasks.Add(Task.Run(() => _heartbeat.RunAsync(stopToken), CancellationToken.None));
            }

            _runningTasks.Clear();
            _runningTasks.AddRange(loopTasks);
            _state = EngineState.Running;

            _logger.LogInformation(
                "Highway engine running: node '{Node}', server '{Server}', {Services} services, {Channels} channels, doorbells {Doorbells}",
                // Redacted: this string routinely carries a password now, and Information
                // level reaches every configured log sink (feature 012).
                _options.NodeName, ConnectionStringRedactor.Redact(_options.Server),
                _catalog.AllServices.Count, _catalog.AllChannels.Count,
                _options.DoorbellsEnabled ? "on" : "off");
        }
        catch
        {
            // Startup failed — release anything already created.
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
            _pendingCalls = null;
            _discovery = null;
            _heartbeat = null;
            _stopCts?.Dispose();
            _stopCts = null;
            _workCts?.Dispose();
            _workCts = null;
            _state = EngineState.NotStarted;
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed) return; // post-dispose calls are no-ops

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is EngineState.Stopped or EngineState.NotStarted)
                return; // idempotent / nothing ran

            _state = EngineState.Draining;
            _logger.LogInformation("Highway engine draining (timeout {DrainTimeout})", _options.DrainTimeout);

            // Loops stop taking new work; in-flight processing keeps running.
            _stopCts?.Cancel();

            // Await in-flight work up to the drain timeout.
            var deadline = DateTime.UtcNow + _options.DrainTimeout;
            while (Volatile.Read(ref _activeOperations) > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);

            if (Volatile.Read(ref _activeOperations) > 0)
                _logger.LogWarning("{Active} operation(s) still in flight after drain timeout; server lease recovery will redeliver",
                    Volatile.Read(ref _activeOperations));

            // Anything still running now is abandoned to lease recovery.
            _workCts?.Cancel();

            // Give loop tasks a brief moment to observe cancellation.
            if (_runningTasks.Count > 0)
            {
                await Task.WhenAny(Task.WhenAll(_runningTasks), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
                    .ConfigureAwait(false);
            }

            // Announce departure while the connection is still up, so operators
            // see the node leave now rather than after the expiry window.
            // Best effort — shutdown never blocks or fails on it.
            if (_heartbeat is not null)
                await _heartbeat.DepartAsync().ConfigureAwait(false);

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
            _pendingCalls = null;
            _discovery = null;
            _heartbeat = null;
            _stopCts?.Dispose();
            _stopCts = null;
            _workCts?.Dispose();
            _workCts = null;
            _runningTasks.Clear();
            _state = EngineState.Stopped;

            _logger.LogInformation("Highway engine stopped");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Tracks an in-flight processing operation for drain purposes.</summary>
    internal void BeginOperation() => Interlocked.Increment(ref _activeOperations);

    /// <summary>Marks an in-flight operation complete.</summary>
    internal void EndOperation() => Interlocked.Decrement(ref _activeOperations);

    private async Task RunTrackedAsync(Func<Task> body)
    {
        BeginOperation();
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Loops wait on their wake with this timeout as self-healing insurance:
    /// even if every doorbell AND the sweeper were lost, drains still happen.
    /// </summary>
    private TimeSpan SelfHealTimeout =>
        TimeSpan.FromMilliseconds(Math.Max(2500, _options.BackstopInterval.TotalMilliseconds * 5));

    public async ValueTask DisposeAsync()
    {
        // Idempotent: DI tracks this instance once per registration
        // (HighwayEngine + IHighwayEngine + IHighwayEngineInternals), so the
        // container may call DisposeAsync more than once.
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    /// <summary>
    /// Warns once when this node processes queues (feature 014).
    ///
    /// <para>A queue whose contents vanish on restart contradicts the point of the concept,
    /// and <c>new HighwayServerBuilder().Build()</c> is memory-only. Feature 016 makes
    /// durability the default; until then the one unacceptable option is a silent lie, so
    /// this says it out loud — once at startup, never per send.</para>
    ///
    /// <para>The client cannot see the server's data directory, so this is phrased as a
    /// reminder rather than a diagnosis. A wrong-but-loud warning beats a right-but-absent
    /// one when the failure mode is silent data loss.</para>
    /// </summary>
    private void WarnIfQueuesAreNotDurable()
        => _logger.LogWarning(
            "This node processes {Count} queue(s). A queue is only durable if the broker was started " +
            "with a data directory — a memory-only server loses queued work on restart. Queues: {Queues}",
            _catalog.AllQueues.Count,
            string.Join(", ", _catalog.AllQueues.Select(q => q.Name)));
}
