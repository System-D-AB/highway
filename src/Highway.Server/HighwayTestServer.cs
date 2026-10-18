using System.Net;
using Highway.Server.Resp;
using Highway.Server.Storage;
using Highway.Server.Storage.Replication;

namespace Highway.Server;

/// <summary>
/// An embedded Highway server for use in integration tests.
///
/// <list type="bullet">
///   <item>Starts automatically on construction.</item>
///   <item>Uses an OS-assigned ephemeral port (no port conflicts between concurrent instances).</item>
///   <item>Memory-only by default — no disk writes. Supply a data directory through the
///         configuration delegate for durability tests.</item>
///   <item>Full HW.* command set served over the same RESP transport as production code.</item>
///   <item>Safe for concurrent instances in the same process.</item>
/// </list>
///
/// Usage:
/// <code>
/// using var server = new HighwayTestServer();
/// services.AddHighway(o => o.Server = server.ConnectionString);
///
/// // With configuration (feature 004.1):
/// using var tuned = new HighwayTestServer(o => o.Lease = TimeSpan.FromMilliseconds(200));
/// </code>
///
/// <para><b>Feature 040:</b> the transport is now the Kestrel RESP server
/// (<see cref="RespServer"/>) over <see cref="IHighwayStore"/> — embedded Garnet is gone. The
/// public surface (<see cref="ConnectionString"/>, <see cref="Port"/>, <see cref="Restart"/>,
/// dispose) is unchanged so every integration test keeps working; only the internals swapped.</para>
/// </summary>
public sealed class HighwayTestServer : IDisposable, IAsyncDisposable
{
    private readonly HighwayServerOptions _opts;
    private RespServer _server;
    private IHighwayStore _store;

    /// <summary>
    /// Connection string valid immediately after construction and stable across
    /// <see cref="Restart"/>.
    ///
    /// <para>Carries the generated password, so a test connects with it transparently.
    /// Never log this — it is a credential-bearing string, which is why the client
    /// redacts every connection string it emits.</para>
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>The TCP port the server listens on (stable across <see cref="Restart"/>).</summary>
    public int Port { get; }

    /// <summary>Initialises and starts a memory-only Highway server on an ephemeral port.</summary>
    public HighwayTestServer() : this(configure: null) { }

    /// <summary>
    /// Initialises and starts a memory-only Highway server on an ephemeral port
    /// with an optional payload-size override (useful for validation tests).
    /// </summary>
    /// <param name="maxPayloadBytes">Override the maximum payload size, or null for the default.</param>
    public HighwayTestServer(int? maxPayloadBytes = null)
        : this(maxPayloadBytes.HasValue
            ? o => o.MaxPayloadBytes = maxPayloadBytes.Value
            : null)
    {
    }

    /// <summary>
    /// Initialises and starts a Highway server on an ephemeral port with full
    /// configuration access. The delegate receives the options object with
    /// <see cref="HighwayServerOptions.Port"/> already set to the probed ephemeral port; the
    /// delegate cannot change the port (the value is re-asserted afterwards) so
    /// <see cref="ConnectionString"/> stays valid.
    /// </summary>
    /// <param name="configure">Optional configuration delegate.</param>
    public HighwayTestServer(Action<HighwayServerOptions>? configure)
    {
        Port = Internal.EphemeralPort.Probe();

        _opts = new HighwayServerOptions
        {
            Port      = Port,
            DataDir   = null,
            Ephemeral = true,   // memory-only by default; a durability test sets DataDir
        };

        // Authenticated by default (feature 012 / 040 R11). The loopback exemption is turned OFF
        // for the test server (exemptLoopback: false below), so the suite exercises AUTH on every
        // connection regardless of the free loopback path production offers. A random credential
        // per instance means no test can accidentally depend on a shared one. The delegate runs
        // first so a test can opt out by clearing the password.
        _opts.Authentication.Password = $"test-{Guid.NewGuid():N}";

        configure?.Invoke(_opts);
        var advertiseOwnPort = _opts.Replication.AdvertiseEndpoint is null;

        // The probed port can be taken by another process between the probe and the bind (the probe
        // must release it for the server to bind it). Retry the whole start on a fresh port — retrying
        // only the probe, as EphemeralPort does, cannot cover that window (063).
        for (var attempt = 1; ; attempt++)
        {
            _opts.Port = Port;    // the delegate cannot change the probed port
            if (advertiseOwnPort) _opts.Replication.AdvertiseEndpoint = $"127.0.0.1:{Port}";
            try
            {
                (_server, _store) = StartServer(_opts, reuseStore: null);
                break;
            }
            catch (Exception e) when (attempt < MaxStartAttempts && IsAddressInUse(e))
            {
                Port = Internal.EphemeralPort.Probe();
            }
        }

        // The address the server actually binds (IPv4 loopback), not "localhost": on a host where
        // localhost resolves to ::1 first, every connect paid a ~2 s failed IPv6 attempt before falling
        // back, and under thread-pool load the fallback timed out (063).
        ConnectionString = _opts.Authentication.IsConfigured
            ? $"127.0.0.1:{Port},password={_opts.Authentication.Password}"
            : $"127.0.0.1:{Port}";
    }

    private const int MaxStartAttempts = 5;

    private static bool IsAddressInUse(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                || x.GetType().Name == "AddressInUseException")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Disposes the inner server and starts a new one on the <b>same port and data directory</b>,
    /// leaving <see cref="ConnectionString"/> valid. With a data directory configured this exercises
    /// recovery (the store reopens the same on-disk path); memory-only, the new server
    /// starts empty (a fresh store).
    /// </summary>
    public void Restart()
    {
        // Dispose first so a durable store releases its directory lock before the new
        // server reopens the same path (recovery). A memory-only restart opens a fresh store, so
        // state is genuinely lost — the two behaviours the durability/memory tests rely on.
        DisposeServerAsync().GetAwaiter().GetResult();
        (_server, _store) = StartServer(_opts, reuseStore: null);
    }

    /// <summary>
    /// Builds the store and starts a RESP server on the configured loopback port. A null
    /// <see cref="HighwayServerOptions.DataDir"/> means an in-memory store (the default); a set one
    /// opens — and, on restart, reopens — a Crystal store whose data survives.
    /// </summary>
    private static (RespServer Server, IHighwayStore Store) StartServer(
        HighwayServerOptions opts, IHighwayStore? reuseStore)
    {
        // 050 T3: a rejoined standby says hello to the learned primary with the node's own shared
        // secret, since the learned rejoin endpoint carries no credentials. Set before Open.
        opts.Replication.AuthTail = string.IsNullOrEmpty(opts.Authentication.Password)
            ? null : $",password={opts.Authentication.Password}";
        // 076: Crystal replicates on its own loopback port, chosen by the OS when first used, under the broker password.
        opts.Replication.DefaultReplicationListen ??= "127.0.0.1:0";
        opts.Replication.ReplicationSecret ??= opts.Authentication.Password;

        // TLS pass-through (040: the fixture-swap gap Kiro's handoff named). A test that sets
        // Tls.CertFileName/CertPassword gets a real TLS endpoint — same RespServer path
        // production uses, non-HTTP ALPN and all. CertSubjectName (store lookup) is a host
        // concern, not a test-server one; TlsOptions.Validate() already rejects both-set.
        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
        if (opts.Tls.CertFileName is { } certFile)
        {
            cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadPkcs12FromFile(certFile, opts.Tls.CertPassword);
        }

        opts.Replication.TlsCertificate = cert;   // 076: replication over TLS when RESP is
        opts.Replication.TlsRequireClientCertificate = opts.Tls.ClientCertificateRequired;

        var store = reuseStore
            ?? (opts.DataDir is { } dir
                ? Storage.Crystal.CrystalHighwayStore.Open(dir, ownsDirectory: false, opts.Replication)
                : new InMemoryStore());

        // The test server authenticates even on loopback (exemptLoopback: false) so every
        // integration test exercises AUTH — the SecurityPolicy posture for the suite.
        var authenticator = new PasswordAuthenticator(opts.Authentication, exemptLoopback: false);

        RespServer server;
        try
        {
            server = RespServer
                .StartAsync(
                    store, opts, authenticator, opts.BindAddress, opts.Port,
                    serverCertificate: cert,
                    clientCertificateRequired: opts.Tls.ClientCertificateRequired)
                .GetAwaiter().GetResult();
        }
        catch when (reuseStore is null)
        {
            // A store opened here must not outlive a failed start: a durable one holds its
            // directory's LOCK, which would fail the retry on a fresh port.
            (store as IDisposable)?.Dispose();
            throw;
        }

        return (server, store);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeServerAsync().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await DisposeServerAsync();

    private async Task DisposeServerAsync()
    {
        await _server.DisposeAsync();
        // A durable store must be disposed to release the directory; the in-memory store's Dispose
        // is a no-op. A durable store's directory is NOT deleted (ownsDirectory: false) so a later
        // Restart / reopen recovers it.
        _store.Dispose();
    }

    /// <summary>042 replication runtime, when this instance is a durable broker.</summary>
    internal HighwayReplication? Replication => (_store as Storage.Crystal.CrystalHighwayStore)?.Replication;

    /// <summary>The durable store, when this instance has a data directory (076: caught-up checks compare its content).</summary>
    internal Storage.Crystal.CrystalHighwayStore? DurableStore => _store as Storage.Crystal.CrystalHighwayStore;

    /// <summary>
    /// Whether every standby holds exactly what <paramref name="primary"/> holds — key for key (076). Stricter than
    /// comparing log positions: a standby is caught up when its data is the primary's.
    /// </summary>
    internal static bool SameData(HighwayTestServer primary, params HighwayTestServer[] standbys)
    {
        try
        {
            var expected = primary.DurableStore!.DumpData();
            return standbys.All(s => s.DurableStore is { } d && Same(expected, d.DumpData()));
        }
        catch (ObjectDisposedException) { return false; }   // a node mid-restart: not caught up yet
    }

    /// <summary>Where <paramref name="standby"/>'s data differs from <paramref name="primary"/>'s — for a caught-up wait that timed out.</summary>
    internal static string DescribeDifference(HighwayTestServer primary, HighwayTestServer standby)
    {
        var p = primary.DurableStore!.DumpData();
        var s = standby.DurableStore?.DumpData() ?? [];
        static string Hex(byte[] b) => Convert.ToHexString(b);
        var pk = p.ToDictionary(e => Hex(e.Key), e => Hex(e.Value));
        var sk = s.ToDictionary(e => Hex(e.Key), e => Hex(e.Value));
        var missing = pk.Keys.Except(sk.Keys).Take(5).ToList();
        var extra = sk.Keys.Except(pk.Keys).Take(5).ToList();
        var changed = pk.Keys.Intersect(sk.Keys).Where(k => pk[k] != sk[k]).Take(5).ToList();
        return $"primary {p.Count} keys, standby {s.Count}; standby role {standby.Replication?.Role}, " +
               $"followed position {standby.Replication?.FollowedPosition()}; missing [{string.Join(", ", missing)}], " +
               $"extra [{string.Join(", ", extra)}], changed [{string.Join(", ", changed)}]; " +
               $"standby transitions: {string.Join(" | ", standby.Replication?.Transitions.TakeLast(6) ?? [])}";
    }

    private static bool Same(List<(byte[] Key, byte[] Value)> a, List<(byte[] Key, byte[] Value)> b)
        => a.Count == b.Count && a.Zip(b).All(p => p.First.Key.AsSpan().SequenceEqual(p.Second.Key) && p.First.Value.AsSpan().SequenceEqual(p.Second.Value));

    /// <summary>
    /// Reads live queue state directly from the in-process store (040 T8). Under Garnet this went
    /// over a self-connection with raw <c>SCAN</c>/<c>LLEN</c>; the RESP server serves no such
    /// commands, and in-process the store is right here, so the read is direct.
    /// </summary>
    internal Task<(string? Unavailable, IReadOnlyList<(string Name, long Depth, long Bytes)> Rows)>
        ReadQueueStateAsync()
    {
        var reader = new StoreBrokerState(_store, _opts);
        return Task.FromResult<(string?, IReadOnlyList<(string, long, long)>)>((null, reader.Queues()));
    }

    /// <summary>
    /// The in-process flight recorder, for tests that need evidence the wire cannot carry —
    /// e.g. claims on a derived queue, whose '@' name HW.REPLAY's identifier rules reject (025).
    /// </summary>
    internal Observability.FlightRecorder Recorder => _server.Recorder;

    /// <summary>
    /// Read-only broker-state inspection for tests (040 fixture swap): the store reads that
    /// used to be raw Redis commands (<c>LLEN</c>/<c>SMEMBERS</c>/<c>GET</c>) against Garnet.
    /// Accepts the old <c>hw:</c> key spellings. Valid across <see cref="Restart"/> — it reads
    /// through the live store field, not a captured one.
    /// </summary>
    internal StoreInspector Inspect => new(_store);

    /// <summary>Reads the classified catalogue directly from the store (022 / 040 T8).</summary>
    internal Task<IReadOnlyList<Observability.CatalogueEntryDto>> ReadCatalogueAsync()
    {
        var reader = new StoreBrokerState(_store, _opts);
        var observed = _server.Recorder.Names().Select(n => n.Name).ToArray();
        return Task.FromResult(reader.Catalogue(observed));
    }

    /// <summary>Reads the registered nodes and what each declared (022 / 040 T8).</summary>
    internal Task<IReadOnlyList<Observability.NodeDto>> ReadNodesAsync()
    {
        var reader = new StoreBrokerState(_store, _opts, _server.Instances);
        return Task.FromResult(reader.Nodes());
    }
}
