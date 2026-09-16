using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Rocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Highway.Server.Resp;

/// <summary>
/// The embedded RESP server host (040 T4/T8): a minimal Kestrel endpoint bound to a loopback port
/// that maps every connection to <see cref="RespConnectionHandler"/>, wired to the real command
/// runtime — an <see cref="IHighwayStore"/>, the striped lock, the doorbell/subscription registry,
/// the flight recorder, and the dispatcher. It is the replacement for embedded Garnet; 037 D3's
/// "Kestrel for the socket".
///
/// <para>This class owns the wiring and lifecycle; <c>HighwayTestServer</c> (T8) and the
/// production host compose on top of it. It intentionally holds no auth/TLS policy of its own
/// beyond what the <see cref="IConnectionAuthenticator"/> it is given expresses.</para>
/// </summary>
internal sealed class RespServer : IRespServerHost, IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SubscriptionRegistry _registry;
    private readonly Storage.Cache.IHighwayCacheStore? _cache;
    private readonly Storage.Cache.CacheSweeper? _cacheSweeper;

    public CommandDispatcher Dispatcher { get; }
    public IConnectionAuthenticator Authenticator { get; }
    public int MaxFrameBytes { get; }

    /// <summary>The store the commands run against — exposed so a test can seed or inspect it.</summary>
    public IHighwayStore Store { get; }

    /// <summary>The flight recorder — exposed for HW.REPLAY-style test hooks.</summary>
    public FlightRecorder Recorder { get; }

    /// <summary>The loopback port Kestrel bound; valid after <see cref="StartAsync"/>.</summary>
    public int Port { get; private set; }

    private RespServer(
        IHighwayStore store,
        HighwayServerOptions options,
        IConnectionAuthenticator authenticator,
        IPAddress bindAddress,
        int port,
        X509Certificate2? serverCertificate,
        bool clientCertificateRequired)
    {
        Store = store;
        Authenticator = authenticator;
        MaxFrameBytes = Math.Max(options.MaxPayloadBytes, options.MaxCatalogBytes) + 4096; // payload + framing headroom
        Recorder = new FlightRecorder(options.Observability);
        _registry = new SubscriptionRegistry();

        var locks = new StripedLock();
        var replication = store is RocksDbStore rocks ? rocks.Replication : null;

        // 044: the broker-local cache — a SEPARATE, non-replicated store. Durable brokers get
        // their own RocksDB at dataDir/cache (by convention, no setting); ephemeral brokers get
        // an in-memory store. The dispatcher routes hw:cache:* to it; it never touches the
        // replicated store, and the replication feeder never learns it exists.
        if (options.Cache.Enabled)
        {
            _cache = store is RocksDbStore && options.DataDir is { } dir
                ? Storage.Cache.RocksDbCacheStore.Open(Path.Combine(dir, "cache"))
                : new Storage.Cache.InMemoryCacheStore();
            _cacheSweeper = new Storage.Cache.CacheSweeper(
                _cache, options.Cache.MaxSizeBytes, options.Cache.SweepInterval);
        }

        Dispatcher = new CommandDispatcher(store, locks, _registry, Recorder, options, replication, _cache);

        // 044 R6: wipe the cache on every epoch change (mastership moved → cached data may be stale).
        if (replication is not null && _cache is not null)
            replication.OnEpochChanged += _cache.Clear;

        if (replication is not null)
        {
            // 042-1c C-T3: the feeder narrates topology changes (GOODBYE, promotions)
            // through the same doorbell surface the herd already subscribes to.
            replication.Narrator = message =>
                _registry.Ring("hw:door:topology", System.Text.Encoding.UTF8.GetBytes(message));

            // 042-1c C-T2: a master registers ITSELF in the roster at startup, so the
            // roster names the whole set — the herd's successor order includes the node
            // it is currently on. Runs before the endpoint opens; single-threaded.
            if (replication.IsWritable)
            {
                RosterStore.TryUpsert(store,
                    new RosterMember(
                        replication.Options.ReplicaId,
                        replication.Options.Priority,
                        replication.SelfEndpoint),
                    out _, out _);
            }
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IRespServerHost>(this);
        builder.Services.AddSingleton<RespConnectionHandler>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(bindAddress, port, listen =>
            {
                if (serverCertificate is not null)
                {
                    // TLS over a raw connection handler (037 R3.3). Crucially, NO ALPN protocol is
                    // advertised: RESP is not HTTP, and a Highway client (SE.Redis) does a plain TLS
                    // handshake then speaks RESP. Kestrel's UseHttps defaults to offering h2/http1.1
                    // over ALPN, so OnAuthenticate clears ApplicationProtocols — the handshake then
                    // negotiates no application protocol, which the TLS test asserts (non-HTTP,
                    // verified not assumed).
                    listen.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = serverCertificate,
                        OnAuthenticate = (_, sslOptions) => sslOptions.ApplicationProtocols = [],

                        // Mutual TLS (feature 012 semantics carried to the 040 transport):
                        // ClientCertificateRequired demands a client certificate at handshake.
                        // Without an issuer chain configured, validation accepts any presented
                        // certificate — exactly the posture SecurityPolicy already warns about
                        // (012), so the option is honoured rather than silently ignored.
                        ClientCertificateMode = clientCertificateRequired
                            ? Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate
                            : Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.NoCertificate,
                        ClientCertificateValidation = (_, _, _) => true,
                    });
                }

                listen.UseConnectionHandler<RespConnectionHandler>();
            });
        });

        _app = builder.Build();
    }

    /// <summary>
    /// Builds and starts an embedded server on <paramref name="bindAddress"/>:<paramref name="port"/>
    /// (port 0 ⇒ an ephemeral loopback port, read back from <see cref="Port"/>). When
    /// <paramref name="serverCertificate"/> is supplied the endpoint is TLS; otherwise plaintext.
    /// </summary>
    public static async Task<RespServer> StartAsync(
        IHighwayStore store,
        HighwayServerOptions options,
        IConnectionAuthenticator authenticator,
        IPAddress bindAddress,
        int port,
        X509Certificate2? serverCertificate = null,
        bool clientCertificateRequired = false,
        CancellationToken cancellationToken = default)
    {
        var server = new RespServer(
            store, options, authenticator, bindAddress, port, serverCertificate, clientCertificateRequired);
        await server._app.StartAsync(cancellationToken);
        server.Port = server.ResolveBoundPort();
        return server;
    }

    private int ResolveBoundPort()
    {
        var feature = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var address = feature?.Addresses.FirstOrDefault();
        // Kestrel reports "http://127.0.0.1:<port>" even for a raw TCP handler; parse the port.
        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            return uri.Port;
        throw new InvalidOperationException("could not resolve the bound RESP port");
    }

    public ISubscriptionSink CreateSubscriber(string connectionId, PipeWriter output)
        => _registry.CreateSubscriber(connectionId, output);

    public void RemoveSubscriber(string connectionId) => _registry.RemoveSubscriber(connectionId);

    /// <summary>The doorbell the command runtime rings (also the subscription registry).</summary>
    public IDoorbell Doorbell => _registry;

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        await _app.DisposeAsync();
        _cacheSweeper?.Dispose();
        _cache?.Dispose();
    }
}
