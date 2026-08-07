using Garnet.server;
using Highway.Server.Commands;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Highway.Server;

/// <summary>
/// Wraps a <see cref="HighwayGarnetServer"/> with full Highway command registration
/// and lifecycle management.
/// </summary>
public sealed class HighwayServer : IHighwayServer
{
    private readonly HighwayGarnetServer _garnet;
    private readonly DoorbellBridge _doorbell;
    private readonly FlightRecorder _recorder;
    private readonly HighwayServerOptions _opts;
    private readonly ILogger<HighwayServer> _logger;
    private bool _started;
    private bool _disposed;

    internal HighwayServer(
        HighwayGarnetServer garnet,
        HighwayServerOptions opts,
        ILoggerFactory? loggerFactory = null)
    {
        _garnet   = garnet;
        _opts     = opts;
        _doorbell = new DoorbellBridge(_garnet);
        _recorder = new FlightRecorder(opts.Observability);
        _logger   = (loggerFactory ?? NullLoggerFactory.Instance)
                        .CreateLogger<HighwayServer>();

        // Register commands BEFORE Start() — required for AOF replay correctness
        RegisterCommands(_garnet, _doorbell, _recorder, _opts);
        _logger.LogInformation("Highway commands registered.");
    }

    /// <inheritdoc/>
    public string Endpoint
        => $"{_opts.BindAddress}:{_opts.Port}";

    /// <inheritdoc/>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _garnet.Start();
        _logger.LogInformation("Highway server ready on {Endpoint}", Endpoint);
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Start();

        // Hold until cancellation is requested
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetResult());

        if (ct.IsCancellationRequested)
            tcs.TrySetResult();

        await tcs.Task.ConfigureAwait(false);
        _logger.LogInformation("Highway server shutting down.");
        Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder.Dispose();
        _garnet.Dispose();
        _logger.LogInformation("Highway server disposed.");
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// One command's registration: the name and arity that
    /// <c>docs/HIGHWAY-PROTOCOL.md</c> documents, plus the factory that builds it.
    /// </summary>
    internal readonly record struct HighwayCommandRegistration(
        string Name, int Arity, Func<CustomTransactionProcedure> Factory);

    /// <summary>
    /// The commands Highway registers, in registration order.
    ///
    /// <para>This list is <b>wiring, not a definition</b>. The protocol itself —
    /// argument shapes, replies, errors, keys, invariants — is defined in
    /// <c>docs/HIGHWAY-PROTOCOL.md</c>. <c>ProtocolConformanceTests</c> parses
    /// that file's Command Index and asserts it against this table in both
    /// directions, so the two cannot drift.</para>
    /// </summary>
    internal static IReadOnlyList<HighwayCommandRegistration> CommandTable(
        HighwayServerOptions opts,
        DoorbellBridge doorbell,
        FlightRecorder recorder) =>
    [
        new("HW.CALL",        4, () => new HwCallCommand(opts, doorbell, recorder)),
        new("HW.REPLY",       3, () => new HwReplyCommand(opts, doorbell, recorder)),
        new("HW.DEQUEUE",     3, () => new HwDequeueCommand(opts, recorder)),
        new("HW.ACK",         4, () => new HwAckCommand(opts, recorder)),
        new("HW.SUBSCRIBE",   3, () => new HwSubscribeCommand(opts, recorder)),
        new("HW.UNSUBSCRIBE", 3, () => new HwUnsubscribeCommand(opts, recorder)),
        new("HW.PUBLISH",     3, () => new HwPublishCommand(opts, doorbell, recorder)),
        new("HW.RECEIVE",    -3, () => new HwReceiveCommand(opts, recorder)),
        new("HW.RACK",        4, () => new HwRackCommand(opts, recorder)),

        // Registry commands (feature 006). Negative arity marks an optional
        // trailing argument: HW.HEARTBEAT's selects the form (absent = liveness,
        // "BYE" = departure, otherwise = catalog); HW.STATS's selects the scope.
        new("HW.HEARTBEAT",  -2, () => new HwHeartbeatCommand(opts, recorder)),
        new("HW.DISCOVER",    2, () => new HwDiscoverCommand(opts)),
        new("HW.STATS",      -1, () => new HwStatsCommand(opts, recorder)),

        // Observability (feature 002). Arity -2: the name is required, and
        // FROM/TO/LIMIT/NODE are optional keyword arguments in any order.
        new("HW.REPLAY",     -2, () => new HwReplayCommand(opts, recorder)),
    ];

    /// <summary>
    /// Registers every HW.* command on the given <paramref name="server"/>.
    /// Called by this class and by <see cref="HighwayTestServer"/>.
    ///
    /// <para><b>Must run before <see cref="Start"/>.</b> Start performs AOF
    /// recovery, which re-executes stored-procedure entries through the
    /// registered set — a command that is not registered yet cannot be replayed.</para>
    /// </summary>
    internal static void RegisterCommands(
        HighwayGarnetServer server,
        DoorbellBridge doorbell,
        FlightRecorder recorder,
        HighwayServerOptions opts)
    {
        foreach (var command in CommandTable(opts, doorbell, recorder))
        {
            server.Register.NewTransactionProc(
                command.Name,
                command.Factory,
                new RespCommandsInfo { Arity = command.Arity });
        }
    }
}
