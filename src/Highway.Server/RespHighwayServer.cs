using System.Net;
using System.Security.Cryptography.X509Certificates;
using Highway.Server.Observability;
using Highway.Server.Resp;
using Highway.Server.Storage;
using Highway.Server.Storage.Rocks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Highway.Server;

/// <summary>
/// The production Highway broker on the post-Garnet stack (feature 041 T1): the 040 Kestrel RESP
/// server (<see cref="RespServer"/>) over the 038 storage engine (<see cref="IHighwayStore"/>).
/// It is the <see cref="IHighwayServer"/> the standalone host boots — the flip that replaces the
/// Garnet-hosted <c>HighwayServer</c> on the running path.
///
/// <para>Composition mirrors what <c>HighwayTestServer</c> does internally: a store built from
/// <see cref="HighwayServerOptions.DataDir"/> (RocksDB when set, in-memory when null), a
/// <see cref="PasswordAuthenticator"/> from the auth options (loopback exempt in production, the
/// C6.x posture), and any in-process components (the dashboard) given a store-backed
/// <see cref="IBrokerState"/> so they read the engine directly rather than over a self-connection
/// (the RESP server serves no raw <c>SCAN</c>/<c>GET</c>).</para>
///
/// <para>The RESP server starts asynchronously; <see cref="Start"/> bridges that for the
/// synchronous host lifecycle, exactly as the test server does.</para>
/// </summary>
public sealed class RespHighwayServer : IHighwayServer
{
    private readonly HighwayServerOptions _opts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RespHighwayServer> _logger;
    private readonly X509Certificate2? _serverCertificate;
    private readonly IReadOnlyList<Func<HighwayComponentContext, IHighwayServerComponent>> _componentFactories;

    private IHighwayStore? _store;
    private RespServer? _server;
    private ReplicaPuller? _puller;
    private IHighwayServerComponent[] _components = [];
    private bool _started;

    internal RespHighwayServer(
        HighwayServerOptions opts,
        ILoggerFactory? loggerFactory,
        X509Certificate2? serverCertificate,
        IReadOnlyList<Func<HighwayComponentContext, IHighwayServerComponent>>? componentFactories)
    {
        _opts = opts;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<RespHighwayServer>();
        _serverCertificate = serverCertificate;
        _componentFactories = componentFactories ?? [];
    }

    /// <inheritdoc/>
    public string Endpoint => $"{_opts.BindAddress}:{_opts.Port}";

    /// <summary>The flight recorder, for in-process components and tests (022).</summary>
    internal FlightRecorder? Recorder => _server?.Recorder;

    /// <inheritdoc/>
    public void Start()
    {
        if (_started) return;

        _opts.Replication.AdvertiseEndpoint ??= $"{_opts.BindAddress}:{_opts.Port}";
        _opts.Replication.Validate();

        // 047: one replication log category, threaded into the store bootstrap, the feeder, and
        // the puller, so replication is visible in logs/ without HW.REPL.STATUS or extra tooling.
        var replLogger = _loggerFactory.CreateLogger("Highway.Replication");

        // Store: RocksDB when a data directory is configured (durable), in-memory otherwise.
        _store = _opts.DataDir is { } dir
            ? Storage.Rocks.RocksDbStore.Open(dir, ownsDirectory: false, _opts.Replication, replLogger)
            : new InMemoryStore();

        if (_store is Storage.Rocks.RocksDbStore durable)
            durable.Replication.Logger = replLogger;

        // Production keeps the loopback exemption (C6.x); the test server turns it off.
        var authenticator = new PasswordAuthenticator(_opts.Authentication, exemptLoopback: true);

        _server = RespServer
            .StartAsync(_store, _opts, authenticator, _opts.BindAddress, _opts.Port, _serverCertificate)
            .GetAwaiter().GetResult();

        if (_store is Storage.Rocks.RocksDbStore rocks &&
            !string.IsNullOrWhiteSpace(_opts.Replication.PrimaryServer))
        {
            _puller = new ReplicaPuller(rocks, _opts.Replication, replLogger);
        }

        // Components (the dashboard) read broker state in-process from the store — never over a
        // self-connection, which the RESP server does not serve for raw commands.
        var brokerState = new StoreBrokerState(_store, _opts);
        var context = new HighwayComponentContext(_opts, _server.Recorder, _loggerFactory, Endpoint, brokerState);
        _components = _componentFactories.Select(f => f(context)).ToArray();
        foreach (var component in _components)
        {
            try { component.Start(); }
            catch (Exception ex)
            {
                // A diagnostic component (the dashboard) must never take down the broker (011 T5).
                _logger.LogError(ex, "Component {Component} failed to start; the broker continues without it.", component.Name);
            }
        }

        _started = true;
        _logger.LogInformation("Highway broker listening on {Endpoint}.", Endpoint);
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Start();
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var component in _components)
        {
            try { component.Dispose(); } catch { /* a component must not block teardown */ }
        }
        _components = [];

        if (_puller is not null)
        {
            await _puller.DisposeAsync().ConfigureAwait(false);
            _puller = null;
        }

        if (_server is not null)
            await _server.DisposeAsync().ConfigureAwait(false);
        _server = null;

        _store?.Dispose();
        _store = null;
    }
}
