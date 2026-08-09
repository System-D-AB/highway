namespace Highway.Server;

/// <summary>
/// An embedded Highway server for use in integration tests.
///
/// <list type="bullet">
///   <item>Starts automatically on construction.</item>
///   <item>Uses an OS-assigned ephemeral port (no port conflicts between concurrent instances).</item>
///   <item>Memory-only by default — no disk writes, no AOF. Supply a data
///         directory through the configuration delegate for durability tests.</item>
///   <item>Full HW.* command set registered via the same path as production code.</item>
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
/// </summary>
public sealed class HighwayTestServer : IDisposable, IAsyncDisposable
{
    private readonly HighwayServerOptions _opts;
    private HighwayServer _server;

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

    /// <summary>
    /// Initialises and starts a memory-only Highway server on an ephemeral port.
    /// </summary>
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
    /// <see cref="HighwayServerOptions.Port"/> already set to the probed
    /// ephemeral port; the delegate cannot change the port (the value is
    /// re-asserted afterwards) so <see cref="ConnectionString"/> stays valid.
    /// Every field of <see cref="HighwayServerOptions"/> except Port is reachable.
    /// </summary>
    /// <param name="configure">Optional configuration delegate.</param>
    public HighwayTestServer(Action<HighwayServerOptions>? configure)
    {
        Port = Internal.EphemeralPort.Probe();

        _opts = new HighwayServerOptions
        {
            Port      = Port,
            DataDir   = null,
            Ephemeral = true,   // 016: durable is the default now, so a test says otherwise
        };

        // Authenticated by default (feature 012). This is what makes the loopback
        // exemption defensible: users get the free path on loopback, and the suite still
        // exercises AUTH on every connection regardless of what they choose. A random
        // credential per instance means no test can accidentally depend on a shared one.
        //
        // The delegate runs first so a test can opt out by clearing the password.
        _opts.Authentication.Password = $"test-{Guid.NewGuid():N}";

        configure?.Invoke(_opts);
        _opts.Port = Port;    // the delegate cannot change the probed port

        _server = CreateServer(_opts);

        // Start on construction so ConnectionString is immediately valid
        _server.Start();

        ConnectionString = _opts.Authentication.IsConfigured
            ? $"localhost:{Port},password={_opts.Authentication.Password}"
            : $"localhost:{Port}";
    }

    /// <summary>
    /// Disposes the inner server and starts a new one on the <b>same port and
    /// data directory</b>, leaving <see cref="ConnectionString"/> valid. With a
    /// data directory configured this exercises AOF recovery; memory-only, the
    /// new server starts empty.
    /// </summary>
    public void Restart()
    {
        _server.Dispose();
        _server = CreateServer(_opts);
        _server.Start();
    }

    private static HighwayServer CreateServer(HighwayServerOptions opts)
    {
        var garnetOpts = HighwayServerBuilder.BuildGarnetOptions(opts);
        var garnet     = new HighwayGarnetServer(garnetOpts);
        return new HighwayServer(garnet, opts);
    }

    /// <inheritdoc/>
    public void Dispose() => _server.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _server.DisposeAsync();

    /// <summary>
    /// Reads live queue state through the same path the dashboard uses (020).
    ///
    /// <para>Exposed on the test server because the read path's whole risk is the security
    /// matrix — open, password, TLS and mTLS — and that has to be provable before any view is
    /// built on it. 018 shipped a self-connection that worked on an open broker and stopped a
    /// TLS one from starting at all.</para>
    /// </summary>
    internal async Task<(string? Unavailable, IReadOnlyList<(string Name, long Depth, long Bytes)> Rows)>
        ReadQueueStateAsync()
    {
        await using var state = new Observability.BrokerState(
            _opts, Microsoft.Extensions.Logging.Abstractions.NullLogger<Observability.BrokerState>.Instance);

        var result = await state.QueuesAsync();

        return (
            result.Unavailable,
            result.Value?.Select(q => (q.Name, q.Depth, q.Bytes)).ToArray()
                ?? []);
    }

    /// <summary>Reads the classified catalogue the way the dashboard does (022).</summary>
    internal async Task<IReadOnlyList<Observability.CatalogueEntryDto>> ReadCatalogueAsync()
    {
        await using var state = new Observability.BrokerState(
            _opts, Microsoft.Extensions.Logging.Abstractions.NullLogger<Observability.BrokerState>.Instance);

        // The observed half comes from the in-process recorder, exactly as the dashboard's will.
        var observed = _server.Recorder.Names().Select(n => n.Name).ToArray();
        var result = await state.CatalogueAsync(observed);

        return result.Value ?? [];
    }

    /// <summary>Reads the registered nodes and what each declared (022).</summary>
    internal async Task<IReadOnlyList<Observability.NodeDto>> ReadNodesAsync()
    {
        await using var state = new Observability.BrokerState(
            _opts, Microsoft.Extensions.Logging.Abstractions.NullLogger<Observability.BrokerState>.Instance);

        var result = await state.NodesAsync();
        return result.Value ?? [];
    }
}
